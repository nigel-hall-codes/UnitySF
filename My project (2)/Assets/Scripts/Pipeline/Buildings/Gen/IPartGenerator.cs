using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// The seam (design #452 D2, #454): a part's geometry is <i>constructed</i> from a parameter
    /// block instead of being loaded from an authored GLB. One implementation per artifact family
    /// — <c>window.double_hung</c>, <c>door.panel</c>, <c>cornice.bracketed</c> — each a
    /// composition of the #453 kernels, not a modelling problem of its own (design #452 D3).
    /// </summary>
    public interface IPartGenerator
    {
        /// <summary>The id a <c>BuildingPart.generatorId</c> resolves against. Dotted
        /// <c>family.variant</c> by convention; matched ordinally, so case matters.</summary>
        string Id { get; }

        /// <summary>Write the part into <paramref name="mb"/> and return its
        /// <see cref="MeshBuilder.Finish"/> result. The builder arrives cleared and must not be
        /// retained. Geometry is authored in part-local space — <b>+X along the facade, +Y up, +Z
        /// outward toward the street</b>, origin at the part's anchor — which is exactly the frame
        /// <c>BuildingAssembler.PlacePart</c> establishes from the facade bearing.</summary>
        PartMesh Generate(PartParams p, MeshBuilder mb);
    }

    /// <summary>
    /// <c>generatorId</c> → <see cref="IPartGenerator"/>. Populated by reflection over the loaded
    /// assemblies, so a new family ships as one class with a parameterless constructor and needs no
    /// registration edit; <see cref="Register"/> exists for tests and for generators that need
    /// constructor arguments.
    /// <para>Not thread-safe — resolution happens on the import thread, like generation itself.</para>
    /// </summary>
    public static class PartGenerators
    {
        static Dictionary<string, IPartGenerator> _byId;

        /// <summary>The generator for <paramref name="generatorId"/>, or false when the id is empty
        /// or unknown. Callers warn and skip; there is deliberately no fallback geometry (#454 —
        /// the placeholder-quad era ends cleanly, no dual-path maintenance).</summary>
        public static bool TryResolve(string generatorId, out IPartGenerator generator)
        {
            generator = null;
            if (string.IsNullOrEmpty(generatorId)) return false;
            EnsureDiscovered();
            return _byId.TryGetValue(generatorId, out generator);
        }

        /// <summary>Every registered id, for diagnostics ("unknown generator 'x'; known: …").</summary>
        public static IEnumerable<string> Ids
        {
            get { EnsureDiscovered(); return _byId.Keys; }
        }

        /// <summary>Add a generator explicitly — for generators that need constructor arguments, and
        /// for tests. Discovery runs first, and the first registration of an id wins, so this can
        /// never silently shadow a discovered generator.</summary>
        public static void Register(IPartGenerator generator)
        {
            if (generator == null) return;
            EnsureDiscovered();
            Add(generator);
        }

        /// <summary>Drop the table so the next resolve re-discovers. For tests, and for editor code
        /// that adds generators after a domain reload.</summary>
        public static void Reset() => _byId = null;

        static void EnsureDiscovered()
        {
            if (_byId != null) return;
            _byId = new Dictionary<string, IPartGenerator>(StringComparer.Ordinal);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }   // partial load: use what resolved
                catch { continue; }
                if (types == null) continue;

                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(IPartGenerator).IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) == null) continue;

                    try { Add((IPartGenerator)Activator.CreateInstance(t)); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[PartGenerators] {t.FullName} could not be constructed: {ex.Message}");
                    }
                }
            }
        }

        static void Add(IPartGenerator generator)
        {
            string id = generator.Id;
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogWarning($"[PartGenerators] {generator.GetType().FullName} has no Id; ignored.");
                return;
            }
            if (_byId.TryGetValue(id, out var existing))
            {
                if (!ReferenceEquals(existing, generator) && existing.GetType() != generator.GetType())
                    Debug.LogWarning($"[PartGenerators] duplicate generator id '{id}': keeping " +
                                     $"{existing.GetType().FullName}, ignoring {generator.GetType().FullName}.");
                return;
            }
            _byId[id] = generator;
        }
    }
}
