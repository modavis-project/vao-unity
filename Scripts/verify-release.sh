#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
DIST="$ROOT/dist"
mkdir -p "$DIST"

node - "$ROOT" <<'NODE'
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const root = process.argv[2];
const read = name => fs.readFileSync(path.join(root, name), 'utf8');
const readJson = name => JSON.parse(read(name));
const pkg = readJson('package.json');
const zenodo = readJson('.zenodo.json');
const codemeta = readJson('codemeta.json');
const publications = readJson('PUBLICATION_IDENTIFIERS.json');
const citation = read('CITATION.cff');
const changelog = read('CHANGELOG.md');
const releaseNotes = read(`.github/release-notes-${pkg.version}.md`);

if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/.test(pkg.version))
  throw new Error(`Invalid semantic package version: ${pkg.version}`);
if (zenodo.version !== pkg.version || codemeta.version !== pkg.version || publications.software.version !== pkg.version)
  throw new Error('Release metadata versions do not match package.json.');
if (!citation.includes(`version: "${pkg.version}"`)) throw new Error('CITATION.cff version does not match package.json.');
if (!changelog.includes(`## ${pkg.version} — ${zenodo.publication_date}`)) throw new Error('CHANGELOG.md version or date does not match release metadata.');
if (!citation.includes(`date-released: "${zenodo.publication_date}"`) || codemeta.datePublished !== zenodo.publication_date)
  throw new Error('Release dates are inconsistent.');
if (!releaseNotes.includes(pkg.version)) throw new Error('GitHub release notes do not identify the package version.');
if (pkg.license !== 'MIT' || !fs.existsSync(path.join(root, 'LICENSE'))) throw new Error('MIT package license metadata is incomplete.');
if (pkg.documentationUrl !== 'https://github.com/modavis-project/vao-unity#readme') throw new Error('Unexpected package documentation URL.');

const softwareDoi = '10.5281/zenodo.22134391';
const standardDoi = '10.5281/zenodo.22122774';
const standardRepository = 'https://github.com/modavis-project/vao-standard';
const candidateCommit = 'd17b3f188fdf7fadd01ba025383e4feca8def935';
const candidateBundle = '82efb6ee31353e72c81671e2c6500c51dc223d7f21af4983705933ea6caa5c96';
if (publications.software.releaseDoi !== softwareDoi || publications.software.releaseDoiStatus !== 'reserved')
  throw new Error('Reserved software DOI metadata is incomplete.');
if (!citation.includes(`doi: "${softwareDoi}"`) || codemeta.identifier !== `https://doi.org/${softwareDoi}`)
  throw new Error('Software DOI is inconsistent across citation metadata.');
const standard = publications.vaoStandard;
if (standard.version !== '0.4.0' || standard.publicationStatus !== 'published' || standard.doi !== standardDoi || standard.canonicalUrl !== standardRepository)
  throw new Error('Published VAO Standard metadata is incomplete.');
if (!codemeta.isBasedOn?.some(item => item.identifier === `https://doi.org/${standardDoi}` && item.url === standardRepository))
  throw new Error('CodeMeta does not identify the published standard.');
const candidate = publications.vaoStandardCandidate;
if (candidate.version !== '0.5.0' || candidate.publicationStatus !== 'candidate' || candidate.commit !== candidateCommit || candidate.normativeBundleSha256 !== candidateBundle || candidate.canonicalUrl !== standardRepository)
  throw new Error('Pinned VAO Standard candidate metadata is incomplete.');
if (!codemeta.isBasedOn?.some(item => item.identifier === `${standardRepository}/tree/${candidateCommit}`))
  throw new Error('CodeMeta does not identify the pinned standard candidate.');
if (!zenodo.related_identifiers.some(item => item.identifier === `https://doi.org/${standardDoi}` && item.relation === 'isSupplementTo'))
  throw new Error('Zenodo metadata does not relate the software to the published standard.');

const expectedHashes = {
  'Editor/Schemas/vao-manifest-0.4.0.schema.json': '3b8fba703654b8f5e42101e2ecc9fca769bf19115d01ae13d044a36c10fcbc83',
  'Editor/Schemas/vao-carrier-0.4.0.schema.json': 'c8b66ed6a5c53592c347bf63314a7a2402096900ace6f8175e477830d7892ac6',
  'Editor/Schemas/vao-materialization-receipt-0.4.0.schema.json': 'c4e6ade12191d4c1fe38b875b6cae653b723a5fa41e8b38b03f44c1c8eff99bf',
  'Editor/Schemas/vao-manifest-0.5.0.schema.json': 'b4ba4dd32c4424abcfe3d47f5eaf2995870a8ef908008b622f6450969dc3e715',
  'Editor/Schemas/vao-carrier-0.5.0.schema.json': '0d5e67ac025fdaf1194ae0b22f5752e4daa275d1e9f894ab3021953e4b0af3e5',
  'Editor/Schemas/vao-materialization-receipt-0.5.0.schema.json': '64f3381395bfcdfa15fdf22cbdeb48be2a2701302137bc49ea26783ac4a360c7',
  'Editor/Schemas/VAO-STANDARD-0.5.0-CANDIDATE.txt': '4882a3b36632ca60ec98d6cd2d8c376829455d656464033710c9d9f26f01620f',
  'Editor/Schemas/VAO-STANDARD-CC-BY-4.0.txt': 'd557539df68e771cc1eedcc91d13f70fca930e508d11eedcafa4b15db49e3744',
  'Tests/Editor/Fixtures/VAO-Standard-Minimal-0.4.0.vao': '1cb8e10c3da1013aacf0e310bfcf60a34959c99ad20e01ece64e3687fa8fe336',
  'Tests/Editor/Fixtures/VAO-Standard-Cuntz-Positiv-0.4.0.json': 'f494397d2c297a59b61f5a09b42b79e641d697ed820014a46f64968e429f5ea1',
  'Tests/Editor/Fixtures/VAO-Standard-Kinoorgel-0.4.0.json': '597e6d4d4055e765b94c269f847054cee43cba79b6090f104f4c075653d93add',
  'Tests/Editor/Fixtures/VAO-Standard-Minimal-0.5.0.vao': '9bc7ff7eb06cd50a66ab5bfeabdecaef68c8b24a15f5b47bc0013811a241403e'
};
for (const [name, expected] of Object.entries(expectedHashes)) {
  const actual = crypto.createHash('sha256').update(fs.readFileSync(path.join(root, name))).digest('hex');
  if (actual !== expected) throw new Error(`Pinned VAO Standard artifact changed: ${name}`);
}

const metadataText = ['CITATION.cff', '.zenodo.json', 'codemeta.json', 'PUBLICATION_IDENTIFIERS.json']
  .map(read).join('\n');
if (/10\.\d{4,9}\/(?:xxxx|todo|tbd|placeholder)/i.test(metadataText)) throw new Error('Placeholder DOI found.');
NODE

if rg -n '/Users/[^/]+|/Volumes/|Ukolov_Transfer|CODE_COLLECTION' "$ROOT/README.md" "$ROOT/Documentation~" "$ROOT/Runtime" "$ROOT/Editor" "$ROOT/Samples~" "$ROOT/Tests"; then
  echo "Public source contains a machine-specific or development-only path." >&2
  exit 1
fi

if rg -n '\bP[012]\b|VaoP[012]' "$ROOT/README.md" "$ROOT/Documentation~" "$ROOT/Runtime" "$ROOT/Editor" "$ROOT/Samples~" "$ROOT/Tests"; then
  echo "Public source contains internal priority labels." >&2
  exit 1
fi

PACK_LIST="$(mktemp)"
trap 'rm -f "$PACK_LIST"' EXIT HUP INT TERM
(cd "$ROOT" && npm pack --dry-run --json) > "$PACK_LIST"
node - "$PACK_LIST" <<'NODE'
const fs = require('fs');
const report = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'))[0];
const paths = report.files.map(item => item.path);
const required = [
  'package.json', 'README.md', 'CHANGELOG.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
  'Runtime/VaoMediaPlayer.cs', 'Runtime/VaoTrackedPlacement.cs', 'Runtime/VaoRuntimeMaterializer.cs',
  'Runtime/VaoRuntimeControlSurface.cs', 'Runtime/VaoLinkedAnimationPlayer.cs', 'Runtime/VaoPresentation.cs',
  'Runtime/VaoOptionalIntegrationAdapters.cs', 'Runtime/VaoDeterministicExecutor.cs', 'Runtime/VaoExecutionData.cs',
  'Runtime/VaoSofaAsset.cs', 'Runtime/VaoSofaDecoder.cs', 'Runtime/VaoConvolutionRenderer.cs',
  'Runtime/ThirdParty/PureHDF.dll', 'Runtime/link.xml', 'Editor/VaoReimport.cs',
  'Editor/VaoContentBrowserWindow.cs', 'Editor/VaoOptionalIntegrationsWindow.cs', 'Editor/VaoSofaImporter.cs',
  'Editor/VaoJsonCanonicalizer.cs', 'Editor/ThirdParty/PureHDF-LICENSE.md',
  'Editor/Schemas/vao-manifest-0.4.0.schema.json', 'Editor/Schemas/vao-carrier-0.4.0.schema.json',
  'Editor/Schemas/vao-materialization-receipt-0.4.0.schema.json', 'Editor/Schemas/VAO-STANDARD-CC-BY-4.0.txt',
  'Editor/Schemas/vao-manifest-0.5.0.schema.json', 'Editor/Schemas/vao-carrier-0.5.0.schema.json',
  'Editor/Schemas/vao-materialization-receipt-0.5.0.schema.json', 'Editor/Schemas/VAO-STANDARD-0.5.0-CANDIDATE.txt',
  'Samples~/OptionalIntegrations/VaoOptionalIntegrationBootstrap.cs',
  'Samples~/DeterministicAcoustics/VaoDeterministicAcousticsDemo.cs',
  'Documentation~/conformance-and-limits.md', 'Documentation~/deterministic-execution.md',
  'Documentation~/acoustic-rendering.md', 'Documentation~/linked-animation-playables.md',
  'Documentation~/presentation-bundles.md', 'Documentation~/optional-integrations.md',
  'Documentation~/VAO-support.md', 'Documentation~/kinoorgel-validation.md'
];
for (const name of required) if (!paths.includes(name)) throw new Error(`Release package is missing ${name}.`);
for (const forbidden of ['Tests/', 'TestProject~/', 'Scripts/', '.github/', 'dist/'])
  if (paths.some(item => item.startsWith(forbidden))) throw new Error(`Release package contains forbidden path ${forbidden}.`);
NODE

(cd "$ROOT" && npm pack --pack-destination "$DIST" >/dev/null)
ARCHIVE="$DIST/org.modavis.vao-unity-$(node -p "require('$ROOT/package.json').version").tgz"
(cd "$DIST" && shasum -a 256 "$(basename "$ARCHIVE")" > "$(basename "$ARCHIVE").sha256")
echo "Verified release artifact: $ARCHIVE"
cat "$ARCHIVE.sha256"
