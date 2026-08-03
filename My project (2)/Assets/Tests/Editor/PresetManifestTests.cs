using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline;

namespace SFMap.Tests
{
    /// <summary>
    /// The importer/browser contract that #469 and #494 broke.
    ///
    /// SFMapImporterWindow itself is an EditorWindow whose import loop is AssetDatabase all the way
    /// down, so it cannot be driven from an edit-mode test. What CAN be tested is the logic those
    /// two bugs actually lived in, which is why it was lifted out of the window into
    /// SFMap.Pipeline: what goes into the preset manifest (#469), when a name mismatch is worth
    /// warning about (#469), which roots a clear has to remove (#494), and when a chunk may be
    /// reused (#494). The wiring — that RunImport calls WritePresetManifest, that ClearGenerated
    /// iterates PresetRoots — is the part these tests do not reach.
    /// </summary>
    [TestFixture]
    public class PresetManifestTests
    {
        static List<ChunkManifestEntry> Entries(params (int col, int row)[] coords)
            => coords.Select(c => new ChunkManifestEntry
            {
                col = c.col, row = c.row,
                worldX = c.col * 500f, worldZ = c.row * 500f,
                minElev = 12.5f, fingerprint = "ABC",
            }).ToList();

        static readonly DateTime Noon = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

        // ------------------------------------------------------------------ #469: manifest content

        [Test]
        public void TheManifestNamesTheFolderItIsWrittenIntoNotTheBakesPreset()
        {
            // The whole point of the field: SFMapPresetsWindow.LoadPreset assigns it to both
            // GeneratedAssets.ActivePreset and ChunkStreamer.preset, so if it disagreed with the
            // folder the manifest sits in, every asset path would resolve into a different preset.
            var source = new PresetManifestJson { preset = "largerbosworth", chunkSize = 500f };

            var built = PresetManifests.Build("larger_bosworth", source, Entries((0, 0)), 0f, Noon);

            Assert.AreEqual("larger_bosworth", built.preset);
        }

        [Test]
        public void TheManifestCarriesEveryImportedChunkWithItsWorldOrigin()
        {
            var built = PresetManifests.Build(
                "wintest",
                new PresetManifestJson { preset = "wintest", chunkSize = 500f },
                Entries((0, 0), (1, 0), (0, 1), (1, 1)),
                minElevation: -3.25f,
                generatedUtc: Noon);

            Assert.AreEqual(4, built.chunks.Length);
            Assert.AreEqual(500f, built.chunkSize);
            Assert.AreEqual(-3.25f, built.minElevation);

            var second = built.chunks[1];
            Assert.AreEqual(1, second.col);
            Assert.AreEqual(0, second.row);
            Assert.AreEqual(500f, second.worldX);
            Assert.AreEqual(0f, second.worldZ);
        }

        [Test]
        public void AnImportThatBuiltNothingStillProducesAWellFormedManifest()
        {
            // Never null chunks: SFMapPresetsWindow reads m.chunks?.Length and LoadPreset warns on
            // an empty list, both of which beat a directory the browser silently skips.
            var built = PresetManifests.Build("empty", null, new List<ChunkManifestEntry>(), 0f, Noon);

            Assert.IsNotNull(built.chunks);
            Assert.AreEqual(0, built.chunks.Length);
            Assert.AreEqual("empty", built.preset);
        }

        [Test]
        public void TheGeneratedStampIsUtcAndSortsLexicographically()
        {
            var earlier = PresetManifests.Build("p", null, Entries((0, 0)), 0f, Noon);
            var later   = PresetManifests.Build("p", null, Entries((0, 0)), 0f, Noon.AddHours(1));

            Assert.AreEqual("2026-08-03T12:00:00Z", earlier.generated);
            Assert.IsTrue(string.CompareOrdinal(earlier.generated, later.generated) < 0);
        }

        [Test]
        public void TheStampIsNormalisedToUtcSoTwoMachinesAgreeOnTheSameInstant()
        {
            // Build() calls ToUniversalTime(), so a local-kind DateTime for the same instant lands
            // on the same string — the "Z" suffix would otherwise be a lie on any non-UTC machine.
            var fromUtc   = PresetManifests.Build("p", null, Entries((0, 0)), 0f, Noon);
            var fromLocal = PresetManifests.Build("p", null, Entries((0, 0)), 0f, Noon.ToLocalTime());

            Assert.AreEqual("2026-08-03T12:00:00Z", fromUtc.generated);
            Assert.AreEqual(fromUtc.generated, fromLocal.generated);
        }

        [Test]
        public void TheManifestSurvivesARoundTripThroughTheSerialiserTheBrowserUses()
        {
            // SFMapPresetsWindow parses the file with JsonUtility, so the fields it lists a preset
            // by (preset, generated, chunks) have to come back out of JsonUtility.ToJson.
            var built = PresetManifests.Build(
                "roundtrip",
                new PresetManifestJson { preset = "roundtrip", chunkSize = 500f },
                Entries((2, 3)), 7.5f, Noon);

            var reparsed = JsonUtility.FromJson<PresetManifestJson>(JsonUtility.ToJson(built));

            Assert.AreEqual("roundtrip", reparsed.preset);
            Assert.AreEqual(built.generated, reparsed.generated);
            Assert.AreEqual(7.5f, reparsed.minElevation);
            Assert.AreEqual(1, reparsed.chunks.Length);
            Assert.AreEqual(2, reparsed.chunks[0].col);
            Assert.AreEqual(3, reparsed.chunks[0].row);
        }

        // ------------------------------------------------------- #469: the mismatch warning

        [Test]
        public void AMismatchedPresetNameIsReportedNamingBothSides()
        {
            string w = PresetManifests.PresetNameMismatchWarning("larger_bosworth", "largerbosworth");

            Assert.IsNotNull(w);
            Assert.IsTrue(w.Contains("larger_bosworth"), w);
            Assert.IsTrue(w.Contains("largerbosworth"), w);
        }

        [Test]
        public void MatchingNamesAndAnAbsentSourcePresetAreBothSilent()
        {
            Assert.IsNull(PresetManifests.PresetNameMismatchWarning("wintest", "wintest"));
            Assert.IsNull(PresetManifests.PresetNameMismatchWarning("wintest", null));
            Assert.IsNull(PresetManifests.PresetNameMismatchWarning("wintest", ""));
        }

        [Test]
        public void CaseAndUnderscoresCountAsAMismatchBecauseAssetPathsTreatThemAsDistinct()
        {
            Assert.IsNotNull(PresetManifests.PresetNameMismatchWarning("WinTest", "wintest"));
            Assert.IsNotNull(PresetManifests.PresetNameMismatchWarning("win_test", "wintest"));
        }

        // --------------------------------------------------- #494: what a clear has to remove

        [Test]
        public void ClearingAPresetHasToCoverTheResourcesRootWhereThePrefabsLive()
        {
            // #494 exactly: the clear only ever removed Assets/Generated/<preset>/, leaving the
            // prefabs AND ChunkManifest.asset (the fingerprint store) behind under Resources.
            string prior = GeneratedAssets.ActivePreset;
            try
            {
                GeneratedAssets.ActivePreset = "wintest";
                var roots = GeneratedAssets.PresetRoots();

                Assert.IsTrue(roots.Contains("Assets/Generated/wintest"),
                              string.Join(", ", roots));
                Assert.IsTrue(roots.Contains("Assets/Resources/Generated/wintest"),
                              string.Join(", ", roots));

                // The three things a half-clear left behind, each of which must now fall inside a
                // root that gets deleted: the fingerprint store, a chunk prefab, and the manifest.
                foreach (string path in new[]
                {
                    GeneratedAssets.ChunkManifestPath(),
                    GeneratedAssets.ChunkPrefabPath(new ChunkCoord(1, 2)),
                    GeneratedAssets.ManifestPath(),
                })
                    Assert.IsTrue(roots.Any(r => path.StartsWith(r + "/", StringComparison.Ordinal)),
                                  $"{path} is not inside any root the clear removes");
            }
            finally { GeneratedAssets.ActivePreset = prior; }
        }

        // ------------------------------------------------ #494 / #261: the freshness decision

        [Test]
        public void AnUnchangedChunkWithBothHalvesOnDiskIsReused()
        {
            Assert.IsTrue(ChunkFreshness.CanReuse("FP", "FP", prefabExists: true,
                                                  generatedAssetsExist: true));
        }

        [Test]
        public void AClearedGeneratedRootForcesARebuildEvenThoughThePrefabSurvived()
        {
            // The regression: fingerprints matched, the prefab was still under Resources, and the
            // chunk was skipped — leaving a prefab pointing at a mesh that had just been deleted.
            Assert.IsFalse(ChunkFreshness.CanReuse("FP", "FP", prefabExists: true,
                                                   generatedAssetsExist: false));
        }

        [Test]
        public void AMissingPrefabStillForcesARebuild()
        {
            Assert.IsFalse(ChunkFreshness.CanReuse("FP", "FP", prefabExists: false,
                                                   generatedAssetsExist: true));
        }

        [Test]
        public void ChangedInputsForceARebuildWithBothHalvesPresent()
        {
            Assert.IsFalse(ChunkFreshness.CanReuse("OLD", "NEW", prefabExists: true,
                                                   generatedAssetsExist: true));
        }

        [Test]
        public void AChunkWithNoRecordedFingerprintIsNeverReused()
        {
            // Pre-#261 manifests carry no fingerprint; an empty string must not match an empty
            // current one either, or every chunk would be skipped on a hash failure.
            Assert.IsFalse(ChunkFreshness.CanReuse(null, "FP", true, true));
            Assert.IsFalse(ChunkFreshness.CanReuse("", "FP", true, true));
            Assert.IsFalse(ChunkFreshness.CanReuse("", "", true, true));
        }

        [Test]
        public void ANormalIncrementalReimportIsUnaffectedByTheWiderGuard()
        {
            // Do not regress #261: with nothing cleared, every unchanged chunk still skips.
            for (int i = 0; i < 8; i++)
                Assert.IsTrue(ChunkFreshness.CanReuse($"FP{i}", $"FP{i}", true, true),
                              $"chunk {i} should still be reused");
        }
    }
}
