# Bundled 7-Zip

This app runs the 7-Zip command-line program as a separate process. The binaries are bundled with the app but
not stored in git.

**Get them after cloning** (PowerShell 7, any OS):

```
pwsh third-party/7zip/fetch.ps1
```

The script downloads the pinned release from the official GitHub releases
(<https://github.com/ip7z/7zip/releases>, linked from 7-zip.org), checks each download against a pinned SHA-256,
unpacks the console builds, and checks them against `SHA256SUMS`. The build then copies them into the app's
output folder under `7zip/`, and the app refuses to run a bundled binary whose hash doesn't match `SHA256SUMS`.

| Platform | File | From |
|---|---|---|
| Windows x86 / x64 / arm64 | `win-*/7za.exe` | `7z<ver>-extra.7z` (standalone console build, no unRAR code) |
| Linux x64 / arm64 | `linux-*/7zz` | `7z<ver>-linux-*.tar.xz` |
| macOS | `osx/7zz` | `7z<ver>-mac.tar.xz` |

## License

7-Zip is copyright (C) Igor Pavlov. Most of it is licensed under the GNU LGPL; some parts are under the BSD
3-clause license. See [`License.txt`](License.txt), which ships with the app.

The bundled binaries are unmodified. Source for the bundled version (see [`VERSION`](VERSION)) is available
from the same release: `7z<ver>-src.7z` at <https://github.com/ip7z/7zip/releases>, or from <https://www.7-zip.org>.

## Upgrading

1. In `fetch.ps1`, change `$Version` and replace the pinned hashes with the ones on the new release page.
2. Run `pwsh third-party/7zip/fetch.ps1 -UpdateChecksums`.
3. Run the tests (`dotnet test`); the 7-Zip integration tests use the bundled copy.
4. Commit `fetch.ps1`, `VERSION`, `SHA256SUMS` and `License.txt`.
