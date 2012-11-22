using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NGit.Api;
using Sharpen;

namespace QualityEnforcerBot
{
    public class Program
    {
        private const int MinutesBetweenUpdates = 1;
        private const int MillisecondsBetweenUpdates = MinutesBetweenUpdates * 60 * 1000;
        private const string BaseRepository = "QualityEnforcer/QualityEnforcerBot";

        private static Timer UpdateTimer { get; set; }
        private static List<int> PendingIssues { get; set; }

        public static string TempDirectory { get; private set; }
        public static List<ForkedRepository> PendingRepositories { get; set; }

        private static void Main(string[] args)
        {
            Console.WriteLine("GitHub Quality Enforcer Bot");

            // Do initialization
            GitHub.Login();
            TempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString().Replace("-", ""));
            Directory.CreateDirectory(TempDirectory);
            PendingRepositories = new List<ForkedRepository>();
            PendingIssues = new List<int>();
            if (File.Exists("session.config"))
            {
                LoadSession();
                Console.WriteLine("Prior session loaded.");
            }

            UpdateTimer = new Timer(DoUpdate, null, Timeout.Infinite, MillisecondsBetweenUpdates);
        }

        private static void DoUpdate(object discarded)
        {
            // Check pending repositories
            for (int i = 0; i < PendingRepositories.Count; i++)
            {
                var repo = PendingRepositories[i];
                if (repo.Expiry < DateTime.Now || GitHub.GetPullRequestStatus(repo.PullRequest, repo.Name) == "closed")
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
            var toFix = issues.Where(i => i.Title.StartsWith("Fix "));
            if (toFix.Count() != 0)
                new Thread(() => FixRepository(toFix.First())).Start();
        }

        private static void FixRepository(Issue issue)
        {
            const string CloneUrl = "git://github.com/{0}.git";
            try
            {
                var repositories = GitHub.GetRepositories();
                var repositoryName = issue.Title.Substring(4).Trim();
                var fullName = GitHub.GetRepositoryFullName(repositoryName);
                if (fullName == null)
                {
                    GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository, "Unable to find " +
                        repositoryName + ". Did you spell it right?");
                    GitHub.CloseIssue(issue.IssueNumber, BaseRepository);
                    return;
                }
                if (repositories.Count(r => r.ToLower() == repositoryName) != 0)
                {
                    GitHub.CommentOnIssue(issue.IssueNumber, BaseRepository,
                        "We're already tracking a repository by that name, try later.");
                    GitHub.CloseIssue(issue.IssueNumber, BaseRepository);
                    return;
                }
                Console.WriteLine("Cloning " + fullName);
                var cloneDir = Path.Combine(TempDirectory, Guid.NewGuid().ToString().Replace("-", ""));

                var repository = CloneRepository(string.Format(CloneUrl, fullName), cloneDir);
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
                    Expiry = DateTime.Parse(repo.Attribute("expiry").Value)
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
                root.Add(element);
            }
            document.Add(root);
            document.Save("session.config");
        }
    }
}
