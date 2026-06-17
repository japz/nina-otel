# NINA Plugin Catalog

NinaOtel is prepared for the NINA plugin catalog by producing two release assets from a version tag:

- `NinaOtel.Plugin.zip`: archive installer containing only the `NinaOtel.*` plugin files at the zip root.
- `manifest.json`: NINA catalog manifest pointing at that archive with a SHA256 checksum.

The release workflow runs on tags matching `v*.*.*`. After a release is created, submit the generated `manifest.json` to the upstream manifest repository:

```text
manifests/n/NinaOtel/3.2.0.9001/0.1.0.2/manifest.json
```

Use the four-part plugin version from `src/NinaOtel.Plugin/Properties/AssemblyInfo.cs` for the final path segment. The manifest `Name` and `Identifier` must continue to match the assembly metadata:

- `Name`: `NinaOtel`
- `Identifier`: `7311D7B5-0BBD-45AB-8E29-DB09B289A798`

Before submitting to the NINA catalog, validate the manifest in a fork of `isbeorn/nina.plugin.manifests`:

```bash
npm install
node gather.js
```

The catalog submission itself is a pull request to `isbeorn/nina.plugin.manifests`; disabling pull requests on this repository does not affect that upstream submission.
