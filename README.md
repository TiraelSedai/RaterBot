# RaterBot
Simple bot that allows to upvote and downvote photos, videos and overall text messages, e.g. memes.

### Automatic download
It also downloads videos from TikTok, VK, Reddit & Instagram automatically. This works pretty inconsistently and if you know a good way to mitigate that - please help me in Issues.
In order to download from Reddit 18+ or Insta consistently, add cookies extracted using [this plugin](https://github.com/rotemdan/ExportCookies) to db/cookies.txt.

## Usage
### Hosted by me
Just add [@mediarater_bot](https://t.me/mediarater_bot) to your group and give it admin permissions.

Bot is hosted in Netherlands so for example for TikTok download it will only work for whatever is available in this region.

### Self-hosted
#### Docker
Get from [GitHub Container Registry](https://github.com/TiraelSedai/RaterBot/pkgs/container/raterbot) or build from sources.
You have to specify TELEGRAM_MEDIA_RATER_BOT_API env variable.

#### As standalone binary
On top of TELEGRAM_MEDIA_RATER_BOT_API variable, you need to have yt-dlp installed and have the binary in PATH.
There should be also gallery-dl, ffmpeg & python installed. Please, refer to Dockerfile and see what gets apt installed.

#### Arm64 (Apple M1 and other)
Is not tested, but should work perfectly fine.

#### Configuration
TELEGRAM_MEDIA_RATER_FORWARD environment variable in format long>long;long>long

where first part is to forward from where (e.g. chat id) to where (e.g. channel id)