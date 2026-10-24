# Contributing to OpenMesh Gaming

Thanks for taking the time to contribute! This project is a self-hosted virtual LAN
mesh for low-latency gaming, and contributions of all kinds are welcome.

## 📌 Reporting Bugs

Please open an issue and include:

- Your OS and version (Raspberry Pi OS / Debian / Ubuntu / Windows 10 / 11)
- OpenMesh Gaming version or commit SHA
- The exact steps to reproduce
- Relevant log output (server stdout, or the client terminal panel)

## 🔧 Pull Requests

1. Fork the repository and create a feature branch:
   ```bash
   git checkout -b feat/your-feature
   ```
2. Keep the change focused — one logical change per PR.
3. Follow the existing code style (4-space indent, descriptive names).
4. Make sure both components still build:
   ```bash
   # Server (Python 3, no dependencies)
   python3 -m py_compile server/server.py

   # Client (Windows only, uses the built-in C# compiler)
   cd client && build.bat
   ```
5. Update the documentation (both `README.md` and `README.fa.md`) if you change
   user-facing behaviour.
6. Open a PR describing **what** you changed and **why**.

## ⚖️ Commit Messages

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(client): add instant disconnect button
fix(server): resolve moon regeneration on ip change
docs: clarify port forwarding steps
chore: bump default dashboard port
```

## 🔐 Security Notes

- **Never commit secrets.** `authtoken.secret`, `identity.secret`, `*.moon`,
  `config.json` and `banned_members.json` are git-ignored for a reason.
- **Never hardcode credentials.** Dashboard credentials belong in
  `server/config.json`, which is generated at install time.
- If you discover a security issue, please report it privately to the maintainers
  rather than opening a public issue.

## 🎨 Adding Translations

The client UI ships with English and Persian. To add a language:

1. Add the language code to the `currentLanguage` field handling in
   `client/src/Program.cs`.
2. Add matching strings in `ApplyLocalization()`.
3. Add the code to the language dropdown in `ShowSettingsDialog()`.

## 📄 License

By contributing, you agree that your contributions will be licensed under the
MIT License.
