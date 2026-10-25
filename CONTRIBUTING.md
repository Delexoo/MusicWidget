# Contributing to Windows Beats

Thank you for your interest in improving **Windows Beats**. This project is maintained by [Delexoo](https://github.com/Delexoo).

## Ways to help

- **Report bugs** — [Open an issue](https://github.com/Delexoo/beats/issues/new?template=bug_report.md) with steps to reproduce, Windows version, and Beats version.
- **Suggest features** — [Open a feature request](https://github.com/Delexoo/beats/issues/new?template=feature_request.md) describing the problem and your proposed solution.
- **Improve docs** — README, [help page](https://delexoo.github.io/beats/help.html), or in-app copy.
- **Submit code** — fork the repo, branch from `main`, and open a pull request.

## Development setup

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows x64)

```powershell
git clone https://github.com/Delexoo/beats.git
cd beats
dotnet restore
dotnet build -c Release
dotnet run --project MusicWidget -c Release
```

**Installer build:** requires [Inno Setup 6](https://jrsoftware.org/isdl.php)

```powershell
powershell -ExecutionPolicy Bypass -File Installer\Build-Installer.ps1
```

## Pull request guidelines

- Keep changes focused — one feature or fix per PR when possible.
- Match existing code style and naming in the touched files.
- Test on Windows 10 or 11 before submitting.
- Update `CHANGELOG.md` for user-visible changes.

## Releases

Tagged releases (`v*`) trigger GitHub Actions to build `MusicWidget-Setup-x64.exe` and publish a GitHub Release. Version numbers live in `MusicWidget/MusicWidget.csproj` and `version.json`.

## Code of conduct

Be respectful and constructive. Harassment or spam will not be tolerated.

## Questions

- **User guide:** [delexoo.github.io/beats/help.html](https://delexoo.github.io/beats/help.html)
- **Issues:** [github.com/Delexoo/beats/issues](https://github.com/Delexoo/beats/issues)
