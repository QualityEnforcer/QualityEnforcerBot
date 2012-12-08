using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NGit.Api;
using NGit.Transport;
using QualityEnforcer;
using Sharpen;

namespace QualityEnforcerBot
{
   public class Program
   {
      private const int MinutesBetweenUpdates = 1;
      private const int MillisecondsBetweenUpdates = MinutesBetweenUpdates * 60 * 1000;

      public const string BaseRepository = "QualityEnforcer/QualityEnforcerBot";
      public const string OrganizationName = "QualityEnforcer";

      private static Timer UpdateTimer { get; set; }
      private static List<int> PendingIssues { get; set; }

      public static string TempDirectory { get; private set; }
      public static List<ForkedRepository> PendingRepositories { get; set; }

      private static void Main(string[] args)
      {
         Console.WriteLine("GitHub Quality Enforcer Bot");

         // Do initialization
         GitHub.Login();
         //TempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString().Replace("-", ""));
         // Who's the asshat at Microsoft that said you shouldn't be able to delete your own shit from temp
         TempDirectory = "Temp";
         Directory.CreateDirectory(TempDirectory);
         PendingRepositories = new List<ForkedRepository>();
         PendingIssues = new List<int>();
         if (File.Exists("session.config"))
         {
            LoadSession();
            Console.WriteLine("Prior session loaded.");
         }

         UpdateTimer = new Timer(DoUpdate, null, 1000, MillisecondsBetweenUpdates);

         Console.WriteLine("Press q to quit.");
         while (Console.ReadKey(true).KeyChar != 'q') ;
      }

      private static void DoUpdate(object discarded)
      {
         // Check pending repositories
         for (int i = 0; i < PendingRepositories.Count; i++)
         {
            var repo = PendingRepositories[i];
            if (repo.Expiry < DateTime.Now || GitHub.GetPullRequestStatus(repo.PullRequest, repo.Origin) == "closed")
            {
               // Delete repository
               Console.WriteLine("Deleting " + repo.Name);
               GitHub.DeleteRepository(repo.Name);
               PendingRepositories.Remove(repo);
               i--;
            }
         }
         SaveSession();
         // Check pending issues
         var issues = GitHub.GetActiveIssues(BaseRepository);
         var toFix = issues.Where(i => i.Title.StartsWith("Fix ") && !PendingIssues.Contains(i.IssueNumber));
         if (toFix.Count() != 0)
            new Thread(() => FixRepository(toFix.First())).Start();
         }

         private static void FixRepository(Issue issue)
         {
            const string CloneUrl = "git://github.com/{0}.git";
            try
            {
               PendingIssues.Add(issue.IssueNumber);
               var repositories = GitHub.GetUserRepositories();
               var repositoryName = issue.Title.Substring(4).Trim();
               var fullName = GitHub.GetRepositoryFullName(repositoryName);
               if (fullName == null)
               {
                  GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository, "Unable to find " +
                     repositoryName + ". Did you spell it right?");
                  GitHub.CloseIssue(issue.IssueNumber, BaseRepository);
                  return;
               }
               if (repositories.Count(r => GetName(r) == GetName(repositoryName)) != 0)
               {
                  GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository,
                     "We're already tracking a repository by that name, try later.");
                  GitHub.CloseIssue(issue.IssueNumber, BaseRepository);
                  return;
               }

               // Comment on the issue indicating progress is being made
               GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository, "Working on it.");

               Console.WriteLine("Cloning " + fullName);
               var cloneDir = Path.Combine(TempDirectory, Guid.NewGuid().ToString().Replace("-", ""));
               var repository = CloneRepository(string.Format(CloneUrl, fullName), cloneDir);

               // Run analytics
               Console.WriteLine("Analyzing " + fullName);
               var project = Enforcer.AnalyzeDirectory(cloneDir);
               GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository, QualityEnforcer.Program.GenerateAnalysis(project));

               var details = GitHub.GetIssueBody(issue.IssueNumber, BaseRepository);
               var rules = QualityRules.Parse(details);

               // Run cleanup
               Console.WriteLine("Cleaning up " + fullName);
               var results = Enforcer.EnforceQuality(project, rules);
               var status = GetStatus(repository);
               if (!results.Any || status.IsClean())
               {
                  GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository, "No changes to apply.");
                  GitHub.CloseIssue(issue.IssueNumber, BaseRepository);
                  Directory.Delete(cloneDir, true);
                  Console.WriteLine("No changes to apply.");
                  return;
               }
               var changes = QualityEnforcer.Program.CreateChangeList(results);
               Console.WriteLine("Committing changes");
               DoCommit(repository, "Code quality enforcement");

               var reader = new StreamReader(
                  Assembly.GetExecutingAssembly().GetManifestResourceStream("QualityEnforcerBot.PullRequestTemplate.txt"));
               var template = reader.ReadToEnd();
               reader.Close();

               template = template.Replace("{changes}", changes);

               // Create fork
               Console.WriteLine("Forking repository");
               var fork = GitHub.Fork(repositoryName);
               Console.WriteLine("Pushing to fork");
               var push = repository.Push();
               push.SetCredentialsProvider(new UsernamePasswordCredentialsProvider(GitHub.Username, GitHub.Password));
               push.SetRemote(fork.Remote);
               push.SetRefSpecs(new RefSpec("refs/heads/master:refs/heads/master"));
               push.Call();

               // Create pull request
               int requestNumber = -1;
               int i;
               for (i = 0; i < 10; i++)
               {
                  try
                  {
                     Console.WriteLine("Sending pull request..."); // TODO: Use the default branch on the repo
                     var originOwner = repositoryName.Remove(repositoryName.IndexOf('/'));
                     requestNumber = GitHub.PullRequest(repositoryName, GitHub.Username + ":master",
                        "master", "Code quality enforcement", template);
                     break;
                  }
                  catch
                  {
                     Thread.Sleep(5000);
                     // Most of the errors creating a pull request are from GitHub lagging beind
                     // yes I know this is terrible
                  }
               }
               if (i == 10)
               {
                  GitHub.DeleteRepository(fork.Name);
                  GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository, "Unable to submit pull request.");
                  GitHub.CloseIssue(issue.IssueNumber, BaseRepository);
                  Directory.Delete(cloneDir, true);
                  return;
               }
               Console.WriteLine("Finished " + repositoryName);
               PendingRepositories.Add(new ForkedRepository
               {
                  Expiry = DateTime.Now.AddDays(7),
                  Name = fork.Name,
                  Origin = fullName,
                  PullRequest = requestNumber
               });
               SaveSession();
               GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository, "Done! You have one week to merge the changes before the fork is deleted.");
               GitHub.CloseIssue(issue.IssueNumber, BaseRepository);
               try
               {
                  Directory.Delete(cloneDir, true);
               } catch { }
            }
            catch (Exception e)
            {
               Console.WriteLine("Exception occured during repository processing:");
               Console.WriteLine(e.ToString());
               GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository,
                  "An exception occured while processing this repository:\n" + e.ToString());
               GitHub.CloseIssue(issue.IssueNumber, BaseRepository);
            }
         }

         private static string GetName(string repositoryName)
         {
            if (repositoryName.StartsWith("http://") || repositoryName.StartsWith("https://"))
               repositoryName = repositoryName.Remove(repositoryName.Length - 4);
            return repositoryName.Substring(repositoryName.LastIndexOf('/') + 1).ToLower();
         }

         private static void DoCommit(Git repository, string message)
         {
            var commit = repository.Commit();
            commit.SetAuthor(GitHub.Username, "quality@sircmpwn.com");
            commit.SetMessage(message);
            commit.SetAll(true);
            commit.Call();
         }

         private static Status GetStatus(Git repository)
         {
            var call = repository.Status();
            return call.Call();
         }

         private static Git CloneRepository(string url, string destination)
         {
            // TODO: Shallow clone
            var command = Git.CloneRepository();
            if (!Directory.Exists(destination))
               Directory.CreateDirectory(destination);
            command.SetDirectory(new FilePath(destination));
            command.SetURI(url);
            command.SetCloneSubmodules(false);
            return command.Call();
         }

         private static void LoadSession()
         {
            var document = XDocument.Load("session.config");
            // Fetch repositories pending deletion
            PendingRepositories.Clear();
            foreach (var repo in document.Root.Elements("repository"))
            {
               PendingRepositories.Add(new ForkedRepository
               {
                  Name = repo.Attribute("name").Value,
                  Expiry = DateTime.Parse(repo.Attribute("expiry").Value),
                  Origin = repo.Attribute("origin").Value,
                  PullRequest = int.Parse(repo.Attribute("pullrequest").Value)
               });
            }
         }

         private static void SaveSession()
         {
            var document = new XDocument();
            var root = new XElement("session");
            // Write repositories
            foreach (var repo in PendingRepositories)
            {
               var element = new XElement("repository");
               element.SetAttributeValue("name", repo.Name);
               element.SetAttributeValue("expiry", repo.Expiry);
               element.SetAttributeValue("origin", repo.Origin);
               element.SetAttributeValue("pullrequest", repo.PullRequest);
               root.Add(element);
            }
            document.Add(root);
            document.Save("session.config");
         }
      }
}