# RaterBot
Simple bot that allows to upvote and downvote photos, videos and overall text messages, e.g. memes.

### Automatic download
It also downloads videos from [TikTok](https://www.tiktok.com/) to Telegram automatically.

## Usage
### Hosted by me
Just add [@mediarater_bot](https://t.me/mediarater_bot) to your group and give it admin permissions.

Bot is hosted in Netherlands so for example for TikTok download it will only work for whatever is available in this region.

### Self-hosted
#### Docker
Get from [Docker Hub](https://hub.docker.com/repository/docker/tiraelsedai/raterbot) or build from sources.
You have to specify TELEGRAM_MEDIA_RATER_BOT_API env variable.

#### As standalone binary
On top of TELEGRAM_MEDIA_RATER_BOT_API variable, you need to have [yt-dlp](https://github.com/yt-dlp/yt-dlp) installed and have the binary in PATH.

#### Arm64 (Apple M1 and other)
Is not tested, but should work perfectly fine if yt-dlp works.
