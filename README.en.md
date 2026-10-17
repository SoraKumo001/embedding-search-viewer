# Embedding Search Viewer

[日本語](README.md) | **English**

A Windows app that lets you **search the images, PDFs and videos on your PC by words or by an image**.

You do not need to know the file name: describe **what it looks like or what it is about** — "a red sports car", "a slide with a chart", "sunset by the sea".

- 🔒 **Everything is processed on your own PC.** Your images and documents are never sent anywhere (the only network traffic is the one-time download of the AI model).
- 🖼️ **Search by image** too: find images that look like one you have.
- ⚡ **Very fast with a GPU**: building the index takes about 2–3 minutes for 1,000 images.

---

## Download

Get the latest version from the [Releases page](https://github.com/SoraKumo001/embedding-search-viewer/releases/latest). Pick one of the following.

**Installer (recommended)**: `EmbeddingSearchViewer-<version>-win-x64-setup.exe`
1. Download and run it (no administrator rights needed; it installs under `%LOCALAPPDATA%\Programs`).
2. Start the app from "EmbeddingSearchViewer" in the Start menu.
3. To uninstall, use "Settings" → "Apps". You are asked whether to delete the index and the AI model as well; if you answer "No", they are kept.

**zip (if you do not want to install)**: `EmbeddingSearchViewer-<version>-win-x64.zip`
1. Download it and extract it anywhere (for example `C:\Apps\EmbeddingSearchViewer`).
2. Double-click **`EmbeddingSearchViewer.cmd`** (type: Windows Command Script) at the top of the extracted folder. The roughly 400 files of the app are kept together in the `app` folder.

**What you may see the first time**
- "Windows protected your PC" (SmartScreen): shown because the app is not code-signed yet. You can start it in either of these ways.
  - **To run it**: click "**More info**", then click the "**Run anyway**" button.
  - **To avoid the warning (recommended)**: unblock the zip **before extracting it**. Right-click the zip, open "Properties", tick "**Unblock**", and press OK. In PowerShell:

    ```powershell
    Unblock-File .\EmbeddingSearchViewer-0.1.0-win-x64.zip
    ```

    If you have already extracted it, unblock the whole folder:

    ```powershell
    Get-ChildItem -Recurse C:\Apps\EmbeddingSearchViewer | Unblock-File
    ```

  If you want to be sure that the file you downloaded is the one that was published, check it like this:

```powershell
(Get-FileHash .\EmbeddingSearchViewer-0.1.0-win-x64.zip -Algorithm SHA256).Hash.ToLower()
```

If the value is the same as the one in `SHA256SUMS.txt` on the Releases page, the file is fine.

---

## System requirements

| Item | Details |
|---|---|
| OS | Windows 11 (the tested environment). It is designed to work on Windows 10 (1809 or later), but it has been tested on Windows 11 only |
| CPU | 64-bit (x64). Windows on ARM is not supported |
| GPU | **A DirectML-capable GPU is recommended** (NVIDIA / AMD / Intel, etc.; development and measurements were done on an NVIDIA GeForce RTX 4070 Ti). It also works without a GPU, but building the index is **much slower** (a few seconds per image) |
| Disk | About 2.5 GB (about 0.5 GB for the app, about 1.8 GB for the AI model) |

---

## Getting started

### 1. Get the AI model (first time only)

The first time you start the app, the "**Model**" screen opens. Click "**Start download**".

- The download is about 1.8 GB. It takes from a few minutes to tens of minutes, depending on your connection.
- **You can stop at any time and resume later.** Even if you close the app, the download continues from where it stopped the next time.
- Downloaded files are verified automatically against the published checksums.

### 2. Add the folders you want to search

1. Open "**Library**" from the menu on the left.
2. Click "**Add folder**" and choose a folder with images, PDFs or videos (you can also drag and drop a folder onto the screen).
3. Click "**Start indexing**".

Wait a little until the index has been built. From the second time on, only **new or changed files** are processed, so it finishes quickly.

| Target | What is processed |
|---|---|
| Images (JPEG / PNG / WebP / BMP / GIF / ICO / TIFF) | The content of the image. The orientation of photos from smartphones is taken into account. For GIF, only the first frame is used |
| PDF | The look of each page and the text of each page |
| Office (docx / xlsx / pptx) | The text of the document (for Excel, only text cells). PowerPoint is searched per slide and Excel per sheet. Office does not need to be installed. The preview shows the matched text |
| Videos (mp4 / mkv / webm / wmv / avi / mov / flv / m4v / ts / mts / m2ts / mpg / mpeg / 3gp / ogv) | A scene every 15 seconds (you can change it to 30, 60 or 120 seconds in "Library") |

**You can choose which kinds of files go in the index.** In "Library", under the file types to put in the index, tick only the ones you need among images, PDFs, Office files and videos (all are on by default). A change applies from the next time you build the index. Files already in the index stay (to remove them, press "Clear" and index again).

**Indexing videos takes time.** As a guide, a one-hour video takes about 3–5 minutes (around 300 scenes; several videos are processed at the same time). A movie of several hours takes more than 10 minutes each.
- While it is running, the progress of each video is shown, such as "**scene 420 / 1,050**".
- **If you press "Stop", or close the app, it continues from where it stopped** (scenes that are done are not redone).
- If you have many long videos, a longer "**Video scene length**" makes it faster (the scenes you can search for become coarser; the change applies to videos indexed from now on).

### 3. Search

1. Open "**Search & viewer**" from the menu on the left.
2. Type what you are looking for in the search box at the top and press Enter.
   - Examples: `a red car` / `a slide with a chart` / `a dog running on the beach`
3. The results are listed on the left. Click one to see a preview on the right.

---

## How to use

### Search by image

You can look for images that are similar to one you have. Any of these works.

- Choose an image with "**Search by image**" next to the search box.
- **Drag and drop** an image onto the window.
- **Right-click** an image in the results and choose "Search for images similar to this one".

### Filtering and sorting

Under the search box you can:

- **Also use keyword search**: also searches file names and the text of PDFs.
- **Minimum similarity**: lower it to include more results, raise it to keep only close ones.
- **Sort by**: relevance / similarity / file name.
- **Advanced filters**: narrow down by modification date, size and folder (part of the path).
- Use the clock icon to bring up your **search history**.

### Preview

| Action | Result |
|---|---|
| Mouse wheel | Zoom in / out |
| Drag | Move the image while zoomed in |
| Double-click | Switch between zoomed in and fit to window |
| "Open in another app" | Opens it in the app you normally use |

Videos can be played **from the scene that matched**.

### Video results are grouped by video

Videos are searched scene by scene, so one long video could fill the results. Therefore **the scenes of the same video are shown as one result** (the best matching scene stands for the video), together with a count such as "**12 matching scenes**".

When you select a video result, **the list of matching scenes appears below the preview**.
- Click a scene to play from that scene.
- You can sort by "Best match first" or "Order in the video".
- With the box above the list you can **search only inside that video with different words** (for example, find a video with "sea", then look for "fireworks" in it). Search with an empty box, or press "Back to matching scenes", to return to the list of the first search.

If you do not want the grouping, turn off "**Group scenes by video**" in the search options (every scene is then shown as its own result).

### Right-click menu of a result

Open in another app / Open folder / Copy path / Search for images similar to this one

### When there are many identical images

Images with exactly the same content are shown as one result, with "**Same image: N more**". Blank pages (almost a single colour) are not added to the index.

### Apply folder changes automatically

Turn on "**Watch the folders and update the index automatically**" in "Library", and additions, changes and deletions of files are applied automatically (it is off at first).

### Tidying the library

You can use these buttons in "Library".

- **Refresh**: reloads the statistics.
- **Remove deleted**: removes files that no longer exist from the index.
- **Remove blank**: removes almost single-colour images from the index.
- **Clear**: deletes the whole index (your original files are not touched).

**Images that cannot be read are left out from the next time.** A file that could not be read after several tries is probably damaged, so it is left out of the index automatically from the next indexing (so that the same file is not tried, and time is not wasted, every time). The files that were left out are listed with the reason under "**Files left out of the index**" in "Library".
- If you repair or replace the file so that its size or modification time changes, it is **included again automatically**.
- If the failure was only temporary, press "**Restore**" (one file) or "**Restore all**", and they are tried again the next time you build the index.

### Switching what you search, such as work and hobby (profiles)

With "**Profile**" at the top of the screen you can switch what you search. Each profile has its own **registered folders, index and search history**. You can search your work documents and your hobby photos without mixing them.

- **Switch**: choose from the "Profile" list. The search results and what you were typing are cleared when you switch.
- **Add**: "…" next to it → "New profile...", give it a name (for example Work, Hobby). After it is created, you are switched to it. It is empty, so register folders in "Library" and build the index.
- **Rename**: "…" → "Rename..." (the profile you are using is renamed).
- **Delete**: "…" → "Delete...". The index, cache, folder settings and search history of the chosen profile are deleted. **Your original images, PDFs and videos are not deleted.** The profile in use cannot be deleted.
- **You cannot switch while indexing.** Switch after it has finished or after you have stopped it.

At first there is one profile, "**Default**". An index created by an earlier version is carried over to it. The AI model is shared by all profiles.

### Language

The app is available in Japanese and English. Choose it under "**Settings**" → "**Language**": **Automatic** (follows the display language of Windows, and is the default), **日本語** or **English**. The change takes effect when the app is restarted ("Restart now" restarts it for you).

### Window size and position

When you quit the app, it **remembers the size and position of the window (and whether it was maximized)** and restores them the next time.

- If the previous position is not visible on the current screens (a monitor was removed, the resolution was changed), or the saved values look wrong, the window opens at the **default size (1280 × 840)** at a position chosen by Windows.
- To go back to the default, close the app and delete `%LOCALAPPDATA%\EmbeddingSearchViewer\window.json`.

---

## Where your data is kept, and privacy

- The content of your images, PDFs and videos is **never sent anywhere**. All AI processing is done on this PC.
- The only network traffic is **the one-time download of the AI model** (from Hugging Face).
- All the data the app creates is in this folder:

```
%LOCALAPPDATA%\EmbeddingSearchViewer\
```

| Item | Content |
|---|---|
| `models\` | The AI model (shared by all profiles) |
| `profiles.json` | The list of profiles and the one used last |
| `settings.json` | Settings of the app, such as the language (shared by all profiles) |
| `window.json` | The size and position of the window (shared by all profiles) |
| `profiles\<profile ID>\media_index.db` | The search index |
| `profiles\<profile ID>\library.json` | Settings such as the registered folders |
| `profiles\<profile ID>\search_history.json` | The search history |
| `profiles\<profile ID>\excluded_files.json` | Files left out of the index because they could not be read |
| `profiles\<profile ID>\cache\` | Page images of PDFs and thumbnails of videos |

**Uninstalling**: delete the app folder and the folder above. Your original images, PDFs and videos are never touched.

---

## Troubleshooting

**The model download stopped or failed**
On the same screen, click "Resume download" (or "Retry"). It continues from where it stopped. If there is not enough free disk space, a message is shown.

**Nothing is found when I search**
Check that you added folders in "Library" and ran "Start indexing". The index may still be empty.

**"The index was built with a different embedding model" is shown**
The model that built the index is different from the current one. Press "**Clear**" in "Library", then build the index again with "Start indexing".

**My images, PDFs or videos do not show up in the results**
- Check that the format is supported (see the table above).
- Images with almost no content (blank pages, etc.) are left out on purpose.
- A PDF with a password can be found by its file name only.
- Some videos cannot be read, depending on the codec (they can still be found by file name).

**Indexing is slow**
Without a usable GPU, each image takes a few seconds. A PC with a DirectML-capable GPU is recommended.

**The app does not start and shows "Could not move the existing index to the new location"**
An older version of the app is still running (this happens only the first time you start after updating). Close all its windows (and check in Task Manager that no `EmbeddingSearchViewer.App` is left), then start it again. The index has not been moved, so it is not lost.

**The app does not start**
- Extract the downloaded zip **before** starting the app (it does not run from inside the zip).
- If you moved the extracted folder, start `EmbeddingSearchViewer.cmd` in that folder, or `EmbeddingSearchViewer.App.exe` in its `app` folder.
- A black window that flashes briefly when you run `EmbeddingSearchViewer.cmd` is normal (it starts the app and closes at once).

---

## Not supported

- Image formats such as HEIC / AVIF.
- The contents of archives such as zip / cbz.
- Old Office formats (doc / xls / ppt) and password-protected Office files (they can still be found by file name).
- Searching Office files by how they look (figures, charts, layout).
- Windows on ARM.
- Searching audio.

---

## Licenses and the software used

- The licenses of the third-party software used by this app are collected in `THIRD-PARTY-NOTICES.md` and the `licenses` folder of the distributed zip.
- **FFmpeg** (LGPL) is used to process videos. See `THIRD-PARTY-NOTICES.md` for details.
- The AI model is [onnx-community/embeddinggemma-2-ONNX](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX) (shown as Apache-2.0 on the distributor's page).
- The app itself is under the **MIT License** ([LICENSE](LICENSE)). `LICENSE` is also included in the distributed zip.

---

## For developers

Building, the release procedure, CI, updating the model, performance know-how and troubleshooting are in the [operations note](docs/OPERATIONS.md) (in Japanese).
