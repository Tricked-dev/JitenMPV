# JitenMPV fork workflow

Work directly on the existing `merged` branch. Do not create feature branches or merge branches for new work. Commit completed changes, including user-authorized existing edits, on `merged`.

Keep the current combined features together until the user asks to separate them after their two upstream PRs merge.

Run `dotnet build JitenMPV.sln --no-restore` for application changes. Keep all test projects, harnesses, fixtures, benchmark scripts, and validation reports in the ignored `.private-tests/` directory only. Never commit tests or test artifacts to JitenMPV, including on `merged`. For subtitle changes, run `dotnet run --project .private-tests/JitenMPV.SubtitleChecks`.
