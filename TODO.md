# TODO

- **macOS window on a real Mac.** The sealed ad-hoc bundle has only been checked
  from Windows. Confirm Gatekeeper offers "Open Anyway" (not "damaged"), that it
  records, then drop the pre-release flag.
- **CI.** Build on a tag and attach the release; on the macOS runner, check the
  bundle with `codesign --verify` and `spctl` and launch it.
- **Code signing.** Windows: SignPath Foundation (free for open source) or
  Certum Open Source. macOS: Developer ID and notarization need an Apple
  Developer account.
- **Branch protection** on `main` now that the repository is public.
- **32-bit Raspberry Pi OS** (`linux-arm`), if anyone asks for it.
