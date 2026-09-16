# Third-party licences

The application bundles two typefaces so that it renders identically on every
platform. Both are used unmodified, subset to latin.

## IBM Plex Sans

Copyright 2019 IBM Corp. — <https://github.com/IBM/plex>

Licensed under the SIL Open Font License, Version 1.1.
Full text: [`packages/ui/src/fonts/IBMPlexSans-LICENSE.txt`](packages/ui/src/fonts/IBMPlexSans-LICENSE.txt)

Shipped as `packages/ui/src/fonts/ibm-plex-sans-latin-variable.woff2`.

## IBM Plex Mono

Copyright 2019 IBM Corp. — <https://github.com/IBM/plex>

Licensed under the SIL Open Font License, Version 1.1.
Full text: [`packages/ui/src/fonts/IBMPlexMono-LICENSE.txt`](packages/ui/src/fonts/IBMPlexMono-LICENSE.txt)

Shipped as `packages/ui/src/fonts/ibm-plex-mono-latin-400.woff2` and
`ibm-plex-mono-latin-700.woff2`. Plex Mono has no variable build, so the two
weights the UI uses are bundled as separate static faces.

---

The OFL requires the licence to travel with the font. `scripts/viteFontLicenses.ts`
emits both files, and this one, into every build alongside the fonts themselves,
so a distributed desktop bundle or VSIX carries them without anyone having to
remember. See [docs/packaging.md](docs/packaging.md).

Everything else in this repository is MIT; see [LICENSE](LICENSE).
