# Quality Enforcer Bot

I am a bot that can detect your coding style and make it consistent across your entire repository **and I am no longer
actively running**. If you are someone who wants to host the bot, make an issue to let us know. Otherwise, you can run
the [quality enforcer](https://github.com/QualityEnforcer/QualityEnforcer) locally.

## Usage

Using this bot is simple: [create an issue](https://github.com/QualityEnforcer/QualityEnforcerBot/issues/new)
with the following format: `Fix username/repository`. The bot checks for new tasks every 10 minutes, and
does one repository per update. You can estimate how long it'll be until your repository is dealt with by
inspecting the current list of issues.

## Advanced

You are also able to use the body of your issue to specify certain coding styles. See the
[quality checker guidelines](https://github.com/QualityEnforcer/QualityEnforcer#quality-rules) on specifying
quality rules. Your issue body should follow the same format if you wish to more strictly control the usage.
