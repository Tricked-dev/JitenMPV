# JitenMPV fork workflow

Work directly on the existing `merged` branch. Do not create feature branches or merge branches for new work. Commit completed changes, including user-authorized existing edits, on `merged`.

Keep the current combined features together until the user asks to separate them after their two upstream PRs merge.

Run `dotnet build JitenMPV.sln --no-restore` and `dotnet run --project tests/JitenMPV.SubtitleChecks` for subtitle changes.
