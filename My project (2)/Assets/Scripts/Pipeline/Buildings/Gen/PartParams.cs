using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>One authored parameter. <see cref="text"/>, when non-empty, is the value and
    /// <see cref="value"/> is ignored — that is how symbolic parameters (a <see cref="ProfileId"/>,
    /// a <see cref="MaterialRole"/>, a <see cref="DetailLevel"/>) stay readable in the JSON instead
    /// of being authored as enum ordinals that silently change meaning when the enum is reordered.</summary>
    [Serializable]
    public struct PartParam
    {
        public string name;
        public float value;
        public string text;
    }

    /// <summary>
    /// A generator's parameter block: a flat, name-keyed bag (design #452 D2 — <c>BuildingPart</c>
    /// gains <c>generatorId</c> + <c>parameters</c>, #454).
    ///
    /// <para><b>Why a bag and not a typed per-family class.</b> The block has to survive three
    /// hops — <c>*.part.json</c> → <see cref="JsonUtility"/> → a <c>BuildingPart</c>
    /// <see cref="ScriptableObject"/> → <see cref="PartKey"/> — and each hop rules something out.
    /// <c>JsonUtility</c> does not deserialize <c>[SerializeReference]</c> fields at all, so
    /// polymorphic per-family param classes would need a hand-rolled JSON parser purely to read a
    /// part file. A per-family <c>ScriptableObject</c> subclass moves the polymorphism into the
    /// asset layer but still has to be named in the JSON, so the discriminator cost is paid twice,
    /// and every family then owns a bespoke flattening routine to feed <see cref="PartKey.From"/> —
    /// which takes a <c>float[]</c> precisely because the cache wants a flat vector. The bag has
    /// one shape for all families, hashes without per-family code, and lets a new family ship as a
    /// generator class plus numbers in a JSON file (design #452 D5: neighborhood identity is a
    /// parameter distribution, edited as data).</para>
    ///
    /// <para>The tradeoff, stated plainly: no compile-time schema. A misspelled parameter name
    /// silently takes the generator's default rather than failing to compile. Generators are
    /// expected to read every parameter through <see cref="GetFloat"/>/<see cref="GetEnum"/> with
    /// an explicit fallback, and to document their names — the family's <c>Params</c> struct in
    /// design #452 generators.md §5.1 <i>is</i> that documentation.</para>
    /// </summary>
    [Serializable]
    public sealed class PartParams
    {
        public PartParam[] values;

        /// <summary>Shared empty block — a part with no parameters at all.</summary>
        public static PartParams Empty { get; } = new PartParams { values = Array.Empty<PartParam>() };

        // Cache-key material, derived once. Parts are import-time immutable: the block lives on a
        // BuildingPart asset that is read, never written, during assembly, and PlacePart asks for a
        // key on every one of a chunk's thousands of placements.
        [NonSerialized] string _keySuffix;
        [NonSerialized] float[] _numbers;

        public int Count => values != null ? values.Length : 0;

        public bool Has(string name) => TryFind(name, out _);

        public float GetFloat(string name, float fallback = 0f)
        {
            if (!TryFind(name, out var p)) return fallback;
            if (string.IsNullOrEmpty(p.text)) return p.value;
            return float.TryParse(p.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                ? f : fallback;
        }

        public int GetInt(string name, int fallback = 0)
            => Mathf.RoundToInt(GetFloat(name, fallback));

        public bool GetBool(string name, bool fallback = false)
        {
            if (!TryFind(name, out var p)) return fallback;
            if (!string.IsNullOrEmpty(p.text))
                return bool.TryParse(p.text, out bool b) ? b : fallback;
            return p.value != 0f;
        }

        /// <summary>A symbolic parameter: <c>text</c> parsed as an enum name (case-insensitive), or
        /// the numeric value read as the enum's underlying ordinal when only <c>value</c> was
        /// authored. Anything unrecognised falls back rather than throwing — an authoring typo must
        /// not abort a chunk import.</summary>
        public TEnum GetEnum<TEnum>(string name, TEnum fallback) where TEnum : struct
        {
            if (!TryFind(name, out var p)) return fallback;
            if (!string.IsNullOrEmpty(p.text))
                return Enum.TryParse<TEnum>(p.text, true, out var parsed) &&
                       Enum.IsDefined(typeof(TEnum), parsed) ? parsed : fallback;

            object boxed = Mathf.RoundToInt(p.value);
            return Enum.IsDefined(typeof(TEnum), boxed) ? (TEnum)Enum.ToObject(typeof(TEnum), boxed) : fallback;
        }

        /// <summary>The triangle budget this part is authored at (design #452 D6). Lives in the bag
        /// rather than as a <c>BuildingPart</c> field precisely because the bag is open: the knob
        /// the §6 measurement will tune needed no schema change to exist.</summary>
        public DetailLevel Detail => GetEnum("detail", DetailLevel.Full);

        /// <summary>
        /// The <see cref="PartMeshCache"/> key for running <paramref name="generatorId"/> over this
        /// block. Numeric parameters go into the float vector, so <see cref="PartKey"/>'s 5 mm
        /// quantisation collapses seeded jitter onto one shared mesh; parameter <i>names</i> and
        /// symbolic values go into the key's id string, where they are compared ordinally. Names
        /// must be in the key or <c>{a:1}</c> and <c>{b:1}</c> would share a mesh. Ordering is
        /// canonical (sorted by name), so two authorings that differ only in field order hit the
        /// same cache entry.
        /// </summary>
        public PartKey KeyFor(string generatorId)
        {
            if (_keySuffix == null) BuildKeyMaterial();
            return PartKey.From(generatorId + _keySuffix, Detail, _numbers);
        }

        void BuildKeyMaterial()
        {
            var src = values ?? Array.Empty<PartParam>();

            var order = new int[src.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            // Array.Sort is unstable; the index tie-break makes the canonical order total even when
            // a block repeats a name.
            Array.Sort(order, (a, b) =>
            {
                int c = string.CompareOrdinal(src[a].name ?? string.Empty, src[b].name ?? string.Empty);
                return c != 0 ? c : a.CompareTo(b);
            });

            var sb = new StringBuilder();
            var numbers = new List<float>(src.Length);
            foreach (int i in order)
            {
                var p = src[i];
                sb.Append('|').Append(p.name);
                if (!string.IsNullOrEmpty(p.text)) sb.Append('=').Append(p.text);
                else numbers.Add(p.value);
            }

            _numbers = numbers.ToArray();
            _keySuffix = sb.ToString();
        }

        bool TryFind(string name, out PartParam found)
        {
            if (values != null && !string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (string.Equals(values[i].name, name, StringComparison.Ordinal))
                    {
                        found = values[i];
                        return true;
                    }
                }
            }
            found = default;
            return false;
        }

        /// <summary>Wire DTO → runtime block. Lives here rather than in the Editor importer so the
        /// <c>*.part.json</c> → generator round-trip is testable without the Editor assembly.</summary>
        public static PartParams From(PartParamJson[] src)
        {
            if (src == null || src.Length == 0) return Empty;
            var vals = new PartParam[src.Length];
            for (int i = 0; i < src.Length; i++)
                vals[i] = new PartParam { name = src[i].name, value = src[i].value, text = src[i].text };
            return new PartParams { values = vals };
        }
    }
}
