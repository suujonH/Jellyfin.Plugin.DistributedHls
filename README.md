# Distributed HLS for Jellyfin

[![CI](https://github.com/suujonH/Jellyfin.Plugin.DistributedHls/actions/workflows/ci.yml/badge.svg)](https://github.com/suujonH/Jellyfin.Plugin.DistributedHls/actions/workflows/ci.yml)

A Jellyfin server plugin that offers pre-encoded HLS versions of local video files. The encoding is done in advance by separate [Distributed HLS Worker](https://github.com/suujonH/Jellyfin.DistributedHls.Worker) processes. The plugin decides what has to be encoded, hands out the jobs, verifies and publishes what the Workers upload, and serves the result as additional media sources of the original item: fixed qualities such as 1080p or 720p, and an adaptive *Auto* source. Clients play these sources through their existing HLS code path; Jellyfin's own transcoder is never started for them. Access to the original file can be limited to the local network and to selected users.

Version 0.2.0, plugin GUID `1d46b74f-6daf-4c2c-a28f-52f202679c16`, targetAbi `13.0.0.0`.

## Features

- **Encoding ahead of time by Workers.** Any number of Worker processes poll the server with a Jellyfin API key. The plugin plans the work and tells each Worker the server-side directory for every output; the Worker decides how its files get there (S3, SCP, or a local or shared directory).
- **Content addressing.** Outputs are keyed by the SHA-256 of the source file. Identical files share one set of outputs, and a renamed or moved file is not encoded again.
- **Rendition-level state.** Every video quality, audio track, text subtitle and the preview image is planned, published and deleted on its own, so adding or removing a quality touches only that quality.
- **Verified publication.** Nothing is published before every reported file has been found in the output directory with the reported size and, optionally, modification time. Published outputs can be verified again on demand.
- **Persistent deletion queue.** Obsolete directories are deleted in the background with retries; a failed deletion never pauses job dispatch.
- **Real version items.** Each published quality and *Auto* also exist as Jellyfin video items, alternate versions of the original, for clients that open the selected version as an item of its own.
- **Tokenized HLS delivery.** Master playlists are generated per request from what is published. Every URI carries a signed grant, so no client needs an authentication header. Files are returned as plain file results, which lets a storage plugin such as S3 Storage redirect them to the storage service.
- **Original-file policy.** The original can be limited to the local network and to selected users. Everyone else gets HLS only: file, download, static-stream and real-time transcoding requests for the original are refused.
- **Settings page** with status, job list, *Rebuild work list* and *Verify published outputs*.

## How it works

### Job flow

1. **Library sync.** A coordinator loop (every 5 seconds) keeps the list of source files: the local video files (not `.strm` shortcuts) of the selected libraries. It scans the libraries at startup, when the plugin is enabled, when the library selection changes and on *Rebuild work list*, and follows Jellyfin's library change events in between. Each file is recorded with its item id, library, path, size and modification time.
2. **Probe.** A new file, or one whose path, size or modification time has changed, gets a probe job. A Worker claims it, reads the file (through its own source mapping, or by downloading it from the server), computes its SHA-256 and runs `ffprobe`. The plugin stores the probe result once per SHA-256 and links the item to that hash.
3. **Plan.** From the probe and the configured qualities the plugin derives the renditions the media should have (see [Encoding ladder and output format](#encoding-ladder-and-output-format)). One encode job per SHA-256 covers every rendition that is missing or was published under an older encoding profile.
4. **Encode and upload.** A Worker claims the encode job. The plan names, for each rendition, the directory on the Jellyfin server where its files must end up, for example `{OutputPath}/{media}/video/1080/{attempt}` (see the layout below). The Worker encodes all renditions, then puts each rendition's files into the server-side directory it was given, through its own storage mapping. The plugin never receives output files over HTTP.
5. **Complete (202).** The Worker posts the list of files it uploaded (relative path, size, modification time) with a summary of each rendition. The plugin checks the list against the plan (every planned rendition exactly once, file paths inside the rendition directory, required files present, and summaries consistent with the plan: quality and size, stream index, channels, bit rates, H.264 codec string), stores it, marks the job *Verifying* and answers `202 Accepted`.
6. **Background verification.** A verification service runs after each completion and every 10 seconds. It checks that every listed file exists in the output directory with the listed size and, when `VerifyModifiedTime` is on, a modification time within 2 seconds of the listed one. If a check fails, the job's directories are queued for deletion and the job is queued again, up to `MaxAttempts`.
7. **Publication.** A verified job is published in one database transaction: each rendition points to its new directory, a directory it replaces is queued for deletion after `PlaybackTokenHours` hours, and outputs that are no longer wanted are queued at once. The in-memory index and the version items are updated afterwards.

**Leases.** A claim holds a lease of `LeaseMinutes`, which the Worker renews with heartbeats. A lease that runs out counts as a failed attempt; the directories the Worker was given are deleted one lease length later, because it may still be uploading. A job that is cancelled while it runs (for example because the plugin was disabled) answers the Worker's next heartbeat with `409 Conflict`, and a probe result for a file that has changed in the meantime is refused with `409` as well.

**Files outside the library.** A Worker can also report a file by its hash and probe result (the Worker's `submit` command). The plugin records the media for the given library without a library item and encodes it. Its outputs are deleted after `OrphanRetentionHours` hours unless a library file with the same content appears in the meantime.

### Content addressing and output layout

The media directory is named after the first 32 hexadecimal characters of the source file's SHA-256 (the first 16 bytes read as a big-endian GUID). Every run of an encode job writes into new directories named after its lease token, so a Worker that has lost its lease can never overwrite the result of a later run.

```text
{OutputPath}/
├── .distributed-hls                             marker of the output root
└── {first 32 hex digits of the SHA-256}/
    ├── video/{quality}/{lease token}/           index.m3u8, init.mp4, segment-000000.m4s, ...
    ├── audio/{stream index}/{lease token}/      index.m3u8, init.mp4, segment-000000.m4s, ...
    ├── subtitles/{stream index}/{lease token}/  index.m3u8, subtitle.vtt
    └── preview/{lease token}/                   preview.jpg
```

`{quality}` is the quality name (for example `1080`), `{stream index}` the ffprobe index of the stream in the source file, and `{lease token}` 32 lowercase hexadecimal characters. The plugin only ever deletes directories that match this layout. Because the key is the content, two items with identical files share one set of outputs, and a renamed or moved file is matched to its existing outputs as soon as its new probe reports the same hash.

### Rendition-level state

A media has one rendition per quality (`v1080`, `v720`, ...), per audio track (`a{stream index}`), per text subtitle (`s{stream index}`), and one `preview`. Each rendition is *Missing*, *Published* or *Failed* on its own:

- Adding a quality creates only the new video rendition and one job for it. Removing a quality withdraws only that rendition and queues its directory for deletion at once. Audio, subtitles and the other qualities are not touched.
- A rendition published under another encoding profile (the current profile is `h264-aac-sdr-v3`) is encoded again and replaced.
- When a job reaches `MaxAttempts`, its missing renditions become *Failed* and are retried only after *Rebuild work list*.
- An item becomes playable as soon as at least one video rendition, and at least one audio rendition if the source has audio, are published.

### Deletion queue

Directories to delete are stored in the database together with the earliest time they may go. A deletion service picks up to 16 due entries every 10 seconds, deletes up to 4 at a time, and then removes their parent directories if they are empty. A failure postpones only that entry, by 1, 2, 4, ... minutes up to 6 hours. Job dispatch never waits for the queue. While the output root cannot be trusted (see [OutputPath](#outputpath)), nothing is deleted.

### Playback flow

1. **PlaybackInfo.** The client calls `POST /Items/{id}/PlaybackInfo` as usual and receives the managed source list (see [Media sources and original-file policy](#media-sources-and-original-file-policy)). Each HLS source is described as HLS transcoding: `SupportsDirectPlay` and `SupportsDirectStream` are false, `TranscodingSubProtocol` is `hls`, `TranscodingContainer` is `mp4`, and `TranscodingUrl` is `Plugins/DistributedHls/Hls/{token}/{playlist}.m3u8?PlaySessionId=...&MediaSourceId=...`. Text subtitles are offered as external WebVTT streams. The token is an HMAC-signed grant for the original item, valid for `PlaybackTokenHours` hours. It is a bearer token: whoever has the URL can load that item's HLS until the grant expires.
2. **Tokenized master playlist.** `GET /Plugins/DistributedHls/Hls/{token}/master.m3u8` lists every published quality, `{quality}p.m3u8` (for example `720p.m3u8`) one quality, and a `-a{stream index}` suffix (`master-a1.m3u8`) limits the playlist to one audio track. The playlist is generated per request from the renditions that are published at that moment and sent with `Cache-Control: no-store`. Its URIs are relative: `r/video/{quality}/index.m3u8`, `r/audio/{stream index}/index.m3u8` and `r/subtitles/{stream index}/index.m3u8`.
3. **Media playlists.** `GET /Plugins/DistributedHls/Hls/{token}/r/{kind}/{index}/index.m3u8` returns the playlist from the currently published directory of that rendition as a plain file result, with `Cache-Control: no-store`.
4. **Segments.** The URIs inside a media playlist are relative as well, so `init.mp4`, the segments and `subtitle.vtt` are requested from the same `r/{kind}/{index}/` path with the grant in it. They are returned as file results with `Cache-Control: private, max-age=86400` and range support.
5. **S3 redirect.** If `OutputPath` lies in a mapping of the [S3 Storage](https://github.com/suujonH/Jellyfin.Plugin.S3Storage) plugin with a public delivery mode, that plugin turns the file response for a media playlist into a redirect to the storage service; in its Worker and pre-signed modes the link authorizes the playlist's whole directory. The client then loads `init.mp4` and the segments relative to the storage address, directly from the storage service. Jellyfin serves only the master playlist and one redirect for each media playlist and subtitle file.

The static `Path` of an HLS source is `/Plugins/DistributedHls/Versions/{version id}/master.m3u8`, which redirects an authenticated user with playback permission to a new token URL. Requests to Jellyfin's own stream routes for an HLS source (`/Videos/{id}/stream` and the dynamic HLS routes such as `/Videos/{id}/master.m3u8`) are redirected to the token URL as well; the audio stream routes answer `404` for HLS sources.

## Encoding ladder and output format

### Ladder

The available qualities are 2160, 1440, 1080, 720, 480, 360, 240 and 144; the default selection is 1080, 720 and 480. Each quality is a 16:9 box (9:16 for portrait material) into which the picture is fitted without padding:

| Quality | 2160 | 1440 | 1080 | 720 | 480 | 360 | 240 | 144 |
|---|---|---|---|---|---|---|---|---|
| Box | 3840×2160 | 2560×1440 | 1920×1080 | 1280×720 | 854×480 | 640×360 | 426×240 | 256×144 |

- The display size of the source is its width corrected by the sample aspect ratio, with width and height swapped for a rotation of 90° or 270°. Material whose display height exceeds its width is portrait.
- The scale factor is the smaller of box width / picture width and box height / picture height. A quality whose factor is above 1.05 would enlarge the picture and is skipped. A factor between 1 and 1.05 keeps the picture at its own size. Output width and height are rounded to even numbers.
- A quality that would produce the same size as a higher one is skipped.
- The frame rate is the source rate, at most 60. Below 720, a rate above 30 is divided by ⌈rate / 30⌉ (60 becomes 30, 50 becomes 25).
- If none of the selected qualities fits, nothing is encoded for the file and it gets no HLS sources.

Examples: a 1280×720 source with the default selection gets 1280×720 (720) and 854×480 (480); 1080 is skipped. A 3840×1600 source with 2160, 1080 and 720 selected gets 3840×1600, 1920×800 and 1280×534.

### Output format

The Worker (version 0.3.0) encodes video and audio renditions as HLS VOD playlists with fMP4 segments of 6 seconds, with these settings:

| Rendition | Output |
|---|---|
| Video | H.264, 8-bit `yuv420p`, BT.709 SDR. Software encoder `libx264` with `-preset veryfast -crf 23 -bf 16 -b_strategy 1 -sc_threshold 0`. A keyframe every 2 seconds (GOP of 2 × frame rate, `-force_key_frames expr:gte(t,n_forced*2)`, closed GOPs, independent segments). Interlaced frames are deinterlaced with `bwdif`; PQ (`smpte2084`) and HLG (`arib-std-b67`) HDR is tone-mapped to SDR with `zscale` and `tonemap=mobius`; scaling uses Lanczos. Files: `index.m3u8`, `init.mp4`, `segment-000000.m4s`, ... |
| Audio | One rendition per audio track: AAC at 48 kHz, mono at 96 kb/s or stereo at 160 kb/s; tracks with more channels are downmixed to stereo. Same files as video. |
| Subtitles | Text subtitles (`subrip`, `srt`, `ass`, `ssa`, `webvtt`, `mov_text`, `text`) are converted to WebVTT: `subtitle.vtt` (with `X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000`) and an `index.m3u8` that lists it as a single segment. Graphic subtitles (PGS, DVD, DVB) are not converted. |
| Preview | `preview.jpg`: one frame at 5 seconds (at half the duration for files shorter than 10 seconds), 480 pixels wide. It is stored with the outputs and is not used for playback. |

Metadata and chapters of the source are not copied.

The master playlist (`#EXT-X-VERSION:7`, `#EXT-X-INDEPENDENT-SEGMENTS`) contains:

- one `#EXT-X-MEDIA` entry per published audio track (group `audio`), named after the track title (or `音轨 {index}`) followed by ` [{index}]`, with `CHANNELS` and a two-letter `LANGUAGE` where known (`eng` → `en`, `jpn` → `ja`, `zho`/`chi` → `zh`, `fra`/`fre` → `fr`, `deu`/`ger` → `de`, `spa` → `es`, `kor` → `ko`, none → `und`). The default track is the requested one, otherwise the source's default track, otherwise the first;
- one `#EXT-X-MEDIA` entry per published text subtitle (group `subs`, named `字幕 {index}` when the track has no title, `DEFAULT=NO`);
- one `#EXT-X-STREAM-INF` entry per published quality, ordered by bandwidth, with `BANDWIDTH` (peak segment bit rate of the video plus the highest audio bit rate), `AVERAGE-BANDWIDTH` (average video bit rate plus the same audio bit rate), `RESOLUTION`, `FRAME-RATE` and `CODECS="avc1.xxxxxx,mp4a.40.2"`; the `avc1` value is read from the `avcC` box of `init.mp4`.

## Media sources and original-file policy

The plugin manages an item when it is a local video file (not a `.strm` shortcut) in an enabled library. For such an item it builds the media source list itself.

### Order

1. **Original quality** (`原始质量`, or `原始质量 · {name}` when the item has several files), only when the original is allowed for the request.
2. **Fixed qualities**, highest first (`1080p`, `720p`, ...): one source per published video quality.
3. **Auto** (`自动`): all published qualities in one adaptive playlist, reported with the size and bit rate of the highest quality.

The HLS sources appear once the media is playable. A user without the *Allow media playback* permission gets no sources. In PlaybackInfo:

- without a requested source, the preferred source is moved to the front (Auto, or the original when Auto resolves to it); the other sources keep their order;
- an original that Jellyfin can neither play directly on the device nor remux or transcode for the user is left out, so access to the original follows the user's transcoding permissions;
- an empty list is answered with `NoCompatibleStream`. In particular, a user without access to the original cannot play a managed item until its HLS is published.

### When Auto resolves to the original

A PlaybackInfo request for *Auto* is answered with the original instead when all of these hold: the request comes from the local network, the user is allowed to use the original (see below), and Jellyfin can deliver the original to the device, by direct play or, if the user may, by its own remuxing or transcoding. The requested audio and subtitle tracks are then applied to the original. Otherwise, and always outside the local network, Auto plays the HLS source.

### LAN and per-user access

The original is allowed when one of these holds:

- the user is in `OriginalUsers`; this applies on any network;
- `AllowLocalOriginal` is on and the request comes from the local network, as defined by Jellyfin's network settings;
- the plugin is disabled.

### Guard on file, download and stream routes

When the original is not allowed, the plugin answers `403` to:

- every response that would hand out the file of a managed original: `/Items/{id}/File`, `/Items/{id}/Download`, static streams (`/Videos/{id}/stream?static=true`) and any other route that returns the file. The check runs on the response, so it does not depend on the route or its parameters;
- Jellyfin's real-time stream routes for the original: `/Videos/{id}/stream`, the dynamic HLS routes (`/Videos/{id}/master.m3u8` and the others), `/Audio/{id}/stream` and `/Audio/{id}/universal`. A missing or unreadable `mediaSourceId` means the route item itself, so the original's rules apply.

Requests authenticated with an API key are not restricted by the guard.

### Version items

For each original with published HLS the plugin creates one Jellyfin video item per published quality and one for Auto, in the same folder. They are alternate versions of the original (`PrimaryVersionId` is set; the original does not link to them), so library views keep showing the original. They copy its name, sort name, images, ratings, tags, overview, year and premiere date, and are updated when the original or the published renditions change. A client that opens the selected version as an item of its own (Android TV, for example, fetches the version by its id) finds a real item, and that item lists its own HLS source first. Version items are removed when their quality is withdrawn, when the original leaves the library, when the plugin is disabled and when it is uninstalled.

## Requirements

- A Jellyfin server built from commit [`aec42b345e1a7da25ee5492183f12209338df688`](https://github.com/jellyfin/jellyfin/commit/aec42b345e1a7da25ee5492183f12209338df688) (targetAbi 13.0.0.0, .NET 10). The plugin is built against that source and uses internal server interfaces.
- To build: the .NET 10 SDK and the Jellyfin source at that commit as a sibling folder `../jellyfin`, or `-p:JellyfinSourceRoot=<path>`.
- PowerShell 7 (`pwsh`) for `scripts/Stage-Plugin.ps1`.
- At least one [Distributed HLS Worker](https://github.com/suujonH/Jellyfin.DistributedHls.Worker) 0.3.0 (Worker protocol 2) with ffmpeg, and a Jellyfin API key for it.
- An output directory on the Jellyfin host that Jellyfin can read and write and that the Workers can fill through one of their storage mappings: an S3 bucket mounted on the host (for example by the S3 Storage plugin), a directory the Worker reaches over SSH, or a local or network directory.

## Installation

1. Build the plugin with the Jellyfin source next to this repository (see [Building and testing](#building-and-testing)):

   ```sh
   dotnet build src/Jellyfin.Plugin.DistributedHls -c Release
   ```

2. Stage it into Jellyfin's plugin folder (the `plugins` folder of Jellyfin's data directory, for example `/var/lib/jellyfin/plugins` with the Debian packages):

   ```sh
   pwsh ./scripts/Stage-Plugin.ps1 -PluginDirectory /var/lib/jellyfin/plugins/DistributedHls_0.2.0.0 -TargetRuntime linux-x64
   ```

   | Parameter | Meaning |
   |---|---|
   | `-PluginDirectory` | Required. Target folder; it is created when missing. |
   | `-TargetRuntime` | Runtime identifier whose native SQLite library is copied, for example `linux-x64`, `linux-arm64`, `linux-musl-x64`, `win-x64` or `osx-arm64`. Default: the runtime of the machine that runs the script. Pass it when staging for another system. |

   The script builds the Release configuration (with the Jellyfin source in `../jellyfin`; set the `JellyfinSourceRoot` environment variable for another location) and copies `Jellyfin.Plugin.DistributedHls.dll`, `DistributedHls.Core.dll`, `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.core.dll`, `SQLitePCLRaw.batteries_v2.dll`, `SQLitePCLRaw.provider.e_sqlite3.dll` and the native `e_sqlite3` library of the target runtime. It writes a `meta.json` that lists only the two plugin assemblies, so Jellyfin does not try to load the other files as plugins.

3. Restart Jellyfin. The plugin appears as **Distributed HLS** under Dashboard → Plugins.
4. Create an API key for the Workers (Dashboard → API Keys), configure the plugin and start a Worker.

The plugin keeps its database at `distributed-hls/jobs.db` in Jellyfin's data folder (for example `/var/lib/jellyfin/data/distributed-hls/jobs.db`). If the database cannot be opened, the plugin runs without data and the settings page shows the reason. Uninstalling the plugin deletes its version items; the outputs in `OutputPath` stay.

## Configuration

The settings page (Dashboard → Plugins → Distributed HLS) and the plugin's messages are in Chinese; the label of each setting is listed below.

| Setting | Label | Default | Meaning |
|---|---|---|---|
| `Enabled` | 启用 HLS | off | Turns the plugin on. While it is off, no jobs are handed out (pending and running jobs are cancelled), no HLS sources or version items are offered, and the original-file guard is inactive. Published outputs are kept. |
| `AllowLocalOriginal` | 允许内网使用原始资源 | off | Allows the original to every user whose request comes from the local network. |
| `OriginalUsers` | user list under 原始资源 | none | Users who may use the original on any network. |
| `Libraries` | 媒体库 | none | Libraries whose local video files are processed. None selected means every library. |
| `Qualities` | 预编码质量 | 1080, 720, 480 | Qualities to encode, from 2160, 1440, 1080, 720, 480, 360, 240 and 144. At least one, no duplicates. An empty list is refused; to stop encoding, turn `Enabled` off. |
| `OutputPath` | HLS 输出目录 | `/cache/hls` (on Windows, `hls` in the temporary folder) | Absolute path of the output root on the Jellyfin host. See [OutputPath](#outputpath). |
| `VerifyModifiedTime` | 校验修改时间 | on | Requires each file's modification time to be within 2 seconds of the time the Worker reported. Turn it off for storage that does not keep modification times. |
| `LeaseMinutes` | 任务租约（分钟） | 5 | Lease of a claimed job, 1–60 minutes. Workers renew it with heartbeats. It is also the delay before the directories of an expired lease are deleted. |
| `MaxAttempts` | 最大重试次数 | 3 | Attempts per job, 1–20. A job that fails this often is not retried until *Rebuild work list*. |
| `OrphanRetentionHours` | 未关联成果保留时长（小时） | 72 | 0–8760. Outputs of media that no library item references any more are deleted after this many hours, so a file that is renamed, moved to another library or replaced by an identical copy within that time keeps its outputs. |
| `PlaybackTokenHours` | 播放授权有效期（小时） | 24 | 1–168. Lifetime of a playback grant. An output replaced by a new publication is kept this long before it is deleted. |
| `PlaybackSigningKey` | not shown | generated | Base64 HMAC key of the playback grants, at least 32 bytes. It is generated at the first start; an invalid key is replaced by a new one, which invalidates the grants issued so far. Saving the settings with an empty value keeps the current key. |

### OutputPath

`OutputPath` is the root of all outputs; renditions are stored and found by their path relative to it.

- The root holds a `.distributed-hls` marker file. The plugin creates it when it first finds a published output in the root. A root in which nothing has been published yet is left untouched, because it may be a mount point that must stay empty until it is mounted.
- Before anything is verified, withdrawn or deleted because a file or directory is missing, the plugin checks the root. When the root is missing, unreadable, or lacks the marker while published outputs are absent from it, verification, deletion and re-verification pause, and Status shows the reason. Verifying jobs and queued deletions keep their state meanwhile, and no attempt is used up.
- Changing `OutputPath` moves the data root: published outputs are looked up under the new path. When migrating, copy the marker together with the data.
- For an intentionally new, empty root, create the marker by hand (an empty file named `.distributed-hls`), then run *Verify published outputs*: the renditions missing in the new root are withdrawn and encoded again. The old directory is not cleaned up.

### Status, job list and actions

Below the settings the page shows:

- **Status** (状态): job counts by state (等待 pending, 运行中 running, 校验中 verifying, 完成 complete, 失败 failed, 已取消 cancelled), the number of media, published renditions and directories waiting for deletion; the database error if the database cannot be opened; the reason verification and deletion are paused if the output root is unusable; the result of the startup check of the Jellyfin actions and parameters the plugin relies on; and the last re-verification (time, renditions checked and failed, and why a run stopped early).
- **Job list** (编码任务): the 200 most recently updated jobs with kind (探测 probe, 编码 encode), state, item id or SHA-256, attempts, Worker, last error and time.
- **Rebuild work list** (重建工作列表): cancels the jobs no Worker has claimed yet, resets failed renditions and failed probes, recalculates the renditions of every media and rescans the libraries. Published outputs are kept.
- **Verify published outputs** (校验已发布成果): checks every published rendition again against the file list it was published with. A rendition that fails is withdrawn, its directory is queued for deletion, and it is encoded again. If the output root becomes unusable during the run, the run stops without withdrawing anything more. Only one run can be active at a time.
- **Refresh** (刷新): reloads the status and the job list.

Administrators can use the same functions over HTTP:

| Method and path | Purpose |
|---|---|
| `GET /Plugins/DistributedHls/Status` | Status as shown on the page. |
| `GET /Plugins/DistributedHls/Jobs?limit=200` | The most recently updated jobs (`limit` 1–1000). |
| `POST /Plugins/DistributedHls/Rebuild` | Rebuild work list; answers `202`. |
| `POST /Plugins/DistributedHls/Verify?itemId={id}` | Verify published outputs, of one item when `itemId` is given; answers `202`, or `409` while a run is active. |

## Workers

The encoding is done by the [Distributed HLS Worker](https://github.com/suujonH/Jellyfin.DistributedHls.Worker), a separate command-line program for any machine with ffmpeg and network access to Jellyfin. Its README covers installation, storage mappings and the encoding pipeline.

- **API key.** Create a key under Dashboard → API Keys and configure it in the Worker. The Worker endpoints accept API keys only; requests with a user session are refused.
- **Protocol 2.** Every Worker request must carry the header `X-DistributedHls-Protocol: 2`; any other value is answered with `400` (`Worker 协议版本不匹配：需要 2`). This plugin version speaks protocol 2 only, as does Worker 0.3.0.
- **Storage.** The plan gives the Worker absolute server paths below `OutputPath`. Configure the Worker's storage mappings so that `OutputPath`, as the server sees it, leads to the storage behind that directory.

Every endpoint answers a malformed request with `400` and the reason, and `503` while the database is unavailable.

| Method and path | Request body | Answer |
|---|---|---|
| `GET /Plugins/DistributedHls/Jobs/Protocol` | none | `{"Version": 2}` |
| `POST /Plugins/DistributedHls/Jobs/Poll` | `{Worker, Sha256?, ProbeJson?, ItemId?, LibraryId?}` | `200` with `{Lease, DownloadUrl}`; `204` when there is no job or the plugin is disabled |
| `GET /Plugins/DistributedHls/Jobs/{id}/Input?leaseToken={token}` | none | The source file, with range support; `409` when the lease is gone or the file has changed |
| `POST /Plugins/DistributedHls/Jobs/{id}/Heartbeat` | `{LeaseToken}` | `204`; `409` when the lease is gone |
| `POST /Plugins/DistributedHls/Jobs/{id}/Probe` | `{LeaseToken, Sha256, ProbeJson}` | `204`; `409` when the lease is gone or the file has changed |
| `POST /Plugins/DistributedHls/Jobs/{id}/Complete` | `{LeaseToken, Renditions}` | `202` when the file list is recorded for verification; `409` when the lease is gone |
| `POST /Plugins/DistributedHls/Jobs/{id}/Failed` | `{LeaseToken, Error}` | `204`; `409` when the lease is gone |

- `Worker` is the Worker's name, 1–128 characters.
- With `Sha256` alone, a poll claims only the encode job of that hash (a directed poll, which the Worker uses right after a probe while the input is still at hand).
- With `Sha256` and `ProbeJson`, a poll first registers the file: with `ItemId` for an item of an enabled library that the plugin already knows (`409` until the library sync has reached it), or with `LibraryId` of an enabled library for a file outside the library. For a hash the plugin already knows, a `ProbeJson` that differs from the stored probe is refused.
- `Lease` carries the job id, kind (`Probe` or `Encode`), lease token, expiry, attempt and payload: the source file for a probe job, the encode plan for an encode job. `DownloadUrl` is relative to the server address, so a base URL or reverse-proxy prefix is kept; it is null when the plan has no source file.
- `Renditions` holds one entry per planned rendition: `Key`, `Summary` (quality, size, frame rate, peak and average bit rate and codec for video; stream index, language, title, channels and default flag for audio and subtitles) and `Files` (path relative to the rendition directory, length, modification time).

## Using with S3 Storage (optional)

This plugin does not need [S3 Storage](https://github.com/suujonH/Jellyfin.Plugin.S3Storage), and neither plugin references the other. They combine through the file system:

1. Mount the bucket on the Jellyfin host (S3 Storage can run s3fs for a mapping) and set `OutputPath` to the mapped directory or a directory below it.
2. Give that mapping a public delivery mode in S3 Storage: Cloudflare Worker, pre-signed URLs or public bucket.
3. Give the Worker an `S3` storage mapping for the same directory, bucket and key prefix, so that what it uploads appears in the mount. The Worker sets `x-amz-meta-mtime`, which s3fs reports as the modification time, so `VerifyModifiedTime` can stay on.

For example, with the directory `/mnt/s3/hls` mapped to the key prefix `hls` of the bucket `media`:

| Where | Setting |
|---|---|
| S3 Storage mapping | `LocalPath` `/mnt/s3/hls`, `Bucket` `media`, `KeyPrefix` `hls`, delivery mode `Worker` |
| Distributed HLS | `OutputPath` `/mnt/s3/hls` |
| Worker storage mapping | `"ServerPath": "/mnt/s3/hls"`, `"Type": "S3"`, `"Bucket": "media"`, `"KeyPrefix": "hls"` |

Verification and deletion then work through the mount, media playlists are redirected to the storage, and `init.mp4` and the segments are loaded from the storage directly. Worker downloads of library files that lie in a public mapping are redirected to the storage as well. Without S3 Storage, or with a *Private* mapping, Jellyfin delivers every file itself.

Use s3fs-fuse 1.90, or 1.93 and later. Versions 1.91–1.92 do not list directories that exist only as key prefixes, and the Worker's uploads create exactly such directories.

## Legacy web UI (optional branch)

The plugin works with the stock web client and with native clients. The [`distributed-hls` branch of jellyfin-web](https://github.com/suujonH/jellyfin-web/tree/distributed-hls) changes the legacy item details page in two ways:

- It asks the plugin which sources are HLS sources and which one to preselect (`GET /Plugins/DistributedHls/Items/{id}/PlaybackOptions`, answering `PreferredMediaSourceId` and `ManagedMediaSourceIds`). The page preselects "Auto" (`自动`) in the version selector whenever the item offers it. This happens on the first load of the page only; a version the user has picked is kept. PlaybackInfo then resolves Auto as described above: the original on the local network where it is allowed and playable, HLS otherwise.
- Selecting an HLS source keeps the page (play buttons, played state, context menu) on the original item. The stock page loads the selected version as an item of its own.

Without the branch, the stock page preselects the first source of the list: the original when it is allowed, otherwise the highest quality.

## Client compatibility and known limitations

### Client compatibility

- Clients receive the HLS sources as HLS transcoding (`TranscodingUrl`, sub-protocol `hls`, fMP4) and play them through their existing HLS code path. Some clients therefore label the playback as transcoding, although Jellyfin runs no encoder.
- The `TranscodingUrl` names the selected or default audio track (for example `master-a1.m3u8`), so a client that changes tracks by requesting PlaybackInfo again gets a playlist with just that track. `PlaySessionId` stays in the URL.
- A client that opens the selected version as an item of its own finds a real version item.
- Device testing on Android, Android TV, jellyfin-desktop and Swiftfin (VLC with separate audio/video fMP4 renditions) is still pending.

### Known limitations

- **CPU encoding.** Encoding is done on the CPU with libx264. The Worker accepts hardware H.264 encoders for the final encoding step only (decoding, deinterlacing, scaling and tone mapping still run on the CPU), and those settings have not been verified on real hardware. GPU decoding and filtering, and single-decode multi-output encoding, are not implemented: every quality is a separate ffmpeg run over the source.
- **Formats.** Video is H.264 8-bit SDR only (HDR sources are tone-mapped), audio is at most stereo, subtitles are text only, and nothing is upscaled.
- **Re-encodes.** During a re-encode, for example after an encoding-profile change, sessions served directly by Jellyfin (no public S3 mapping) can mix an old playlist with new segments, because the relative URIs always resolve to the currently published directory.
- **Replaced outputs.** A replaced output is kept for `PlaybackTokenHours` before it is deleted, so both copies take space meanwhile.
- **s3fs mounts.** Real-mount verification on Linux with s3fs is pending: Worker upload, visible in the mount, verified, deleted through the mount, mount loss and recovery.
- **Access before publication.** A user without access to the original cannot play a managed item until its HLS has been published.
- **Server version.** The plugin decorates Jellyfin's media source manager and filters specific controller actions of the server commit it is built against. Another server version needs a rebuild and may need changes. At startup the plugin checks that the actions and parameters it relies on exist, and reports missing ones in Status.

## Building and testing

```sh
git clone https://github.com/jellyfin/jellyfin.git
git -C jellyfin checkout aec42b345e1a7da25ee5492183f12209338df688
git clone https://github.com/suujonH/Jellyfin.Plugin.DistributedHls.git
cd Jellyfin.Plugin.DistributedHls
dotnet build src/Jellyfin.Plugin.DistributedHls -c Release
dotnet test tests/DistributedHls.Core.Tests -c Release
```

The plugin is built against the Jellyfin server source at commit `aec42b345e1a7da25ee5492183f12209338df688` (targetAbi 13.0.0.0, .NET 10). The build expects that source as the sibling folder `../jellyfin`; pass `-p:JellyfinSourceRoot=<path>` when it is elsewhere. Warnings are treated as errors.

`src/DistributedHls.Core` holds the logic that does not depend on Jellyfin: job store, planning, ladder, paths, playlists, grants and verification. Its unit tests in `tests/DistributedHls.Core.Tests` need neither the Jellyfin source nor ffmpeg. The CI workflow in `.github/workflows/ci.yml` runs the same build and tests on Ubuntu.

## Related projects

- [Jellyfin.Plugin.DistributedHls](https://github.com/suujonH/Jellyfin.Plugin.DistributedHls): this plugin.
- [Jellyfin.DistributedHls.Worker](https://github.com/suujonH/Jellyfin.DistributedHls.Worker): the encoding Worker.
- [Jellyfin.Plugin.S3Storage](https://github.com/suujonH/Jellyfin.Plugin.S3Storage): S3 mappings, s3fs mounts and direct delivery from the storage service.
- [jellyfin-web, branch `distributed-hls`](https://github.com/suujonH/jellyfin-web/tree/distributed-hls): optional changes to the legacy web item details page.

The two plugins are independent: each works without the other, and neither references the other.

## License

Licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE).
