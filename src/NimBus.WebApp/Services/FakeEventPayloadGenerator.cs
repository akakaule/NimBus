using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NimBus.Core.Events;

namespace NimBus.WebApp.Services
{
    /// <summary>
    /// Server-side reflection-driven generator that produces a randomized JSON
    /// payload for an <see cref="IEventType"/>. The generated payload is
    /// designed to pass <see cref="IEvent.TryValidate"/> — the same gate the
    /// Compose / Resubmit-with-changes submit path applies.
    ///
    /// Strategy (per spec 007):
    ///   1. Seed from the authored static Example (deep-cloned through
    ///      JsonConvert so the static instance is never mutated).
    ///   2. If no example is authored, construct a fresh instance from the
    ///      parameterless constructor.
    ///   3. Walk every public writable leaf and substitute a type-aware
    ///      random value (enum / Guid / string / numeric / bool / date /
    ///      nested complex / collection element).
    ///   4. Run TryValidate; if invalid, retry up to <see cref="MaxAttempts"/>.
    ///   5. On exhaustion: return the example serialized verbatim (when
    ///      seeded) or the latest random attempt (when not).
    ///
    /// The generator is concurrent-safe: each call uses a fresh
    /// <see cref="Random"/> instance seeded from <see cref="Guid.NewGuid"/>.
    /// MessageMetadata properties are always skipped (the platform stamps
    /// them on send). Index properties and read-only properties are skipped.
    /// [JsonProperty] / [JsonConverter] attributes are honored because
    /// serialization runs through Newtonsoft on the same type.
    /// </summary>
    public class FakeEventPayloadGenerator
    {
        /// <summary>Retry budget for satisfying TryValidate.</summary>
        public const int MaxAttempts = 5;

        /// <summary>Recursion bound for nested complex types.</summary>
        public const int MaxDepth = 12;

        private static readonly string[] FirstNames = new[]
        {
            "Alex", "Sam", "Jordan", "Taylor", "Morgan", "Casey", "Riley", "Quinn",
            "Anna", "Bjarke", "Camilla", "David", "Elisabeth", "Frederik", "Gitte",
            "Henrik", "Ida", "Jens", "Karen", "Lars", "Mette", "Niels", "Ole",
        };

        private static readonly string[] LastNames = new[]
        {
            "Hansen", "Nielsen", "Jensen", "Pedersen", "Andersen", "Sørensen",
            "Larsen", "Christensen", "Olsen", "Schmidt", "Mortensen", "Madsen",
        };

        private static readonly string[] Companies = new[]
        {
            "Contoso", "Acme", "Globex", "Initech", "Umbrella", "Soylent",
            "Stark Industries", "Pied Piper", "Hooli", "Cyberdyne", "Tyrell",
        };

        private static readonly string[] CompanySuffixes = new[]
        {
            "A/S", "GmbH", "Ltd", "Inc", "BV", "AB", "Oy", "SA", "NV",
        };

        private static readonly string[] Countries = new[]
        {
            "DK", "DE", "SE", "NO", "FI", "GB", "FR", "NL", "ES", "IT", "US",
        };

        private static readonly string[] CurrencyCodes = new[]
        {
            "EUR", "USD", "DKK", "SEK", "NOK", "GBP",
        };

        private static readonly string[] Cities = new[]
        {
            "Copenhagen", "Berlin", "Stockholm", "Oslo", "Helsinki", "London",
            "Paris", "Amsterdam", "Madrid", "Milan",
        };

        private static readonly string[] Streets = new[]
        {
            "Strandvejen", "Hauptstrasse", "Kungsgatan", "Karl Johans gate",
            "Mannerheimintie", "Baker Street", "Rue de Rivoli",
        };

        private static readonly string[] EmailDomains = new[]
        {
            "example.com", "example.dk", "example.de", "example.io", "test.local",
        };

        private static readonly string[] PhonePrefixes = new[]
        {
            "+45", "+49", "+46", "+47", "+44", "+33", "+1",
        };

        /// <summary>
        /// Returns an indented JSON payload string for the supplied event type,
        /// or <c>null</c> when the type cannot be constructed (abstract,
        /// interface, no accessible parameterless constructor).
        /// </summary>
        public string? Generate(IEventType eventType)
        {
            ArgumentNullException.ThrowIfNull(eventType);

            var clrType = eventType.GetEventClassType();
            if (clrType is null) return null;

            var random = new Random(Guid.NewGuid().GetHashCode());

            // Seed: prefer the authored Example; fall back to a fresh instance.
            IEvent? example = null;
            try { example = eventType.GetEventExample(); }
            catch { example = null; }

            var seededFromExample = example is not null;
            var buildMissing = !seededFromExample;

            string? lastCandidate = null;
            string? exampleSerialized = null;

            // Make sure we have something to randomize. If neither example nor
            // a constructible bare instance is available, give up cleanly.
            object? template = seededFromExample
                ? DeepClone(example!, clrType)
                : CreateEvent(clrType);

            if (template is null) return null;

            // Capture the unrandomized example as the safe fallback baseline.
            if (seededFromExample)
            {
                exampleSerialized = JsonConvert.SerializeObject(example, Formatting.Indented);
            }

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                // Each attempt randomizes a fresh clone so a previous
                // randomization doesn't pollute the next attempt's state.
                object? candidate = seededFromExample
                    ? DeepClone(example!, clrType)
                    : CreateEvent(clrType);
                if (candidate is null) break;

                try
                {
                    Randomize(candidate, random, depth: 0, buildMissing: buildMissing);
                }
                catch
                {
                    // Reflection / setter blew up on a property — treat the
                    // attempt as a miss, keep trying. The fallback path
                    // ultimately returns the example or the latest candidate.
                    continue;
                }

                lastCandidate = JsonConvert.SerializeObject(candidate, Formatting.Indented);

                if (candidate is IEvent evt)
                {
                    EventValidationResult result;
                    try { result = evt.TryValidate(); }
                    catch
                    {
                        // Poorly-written validator threw. Treat as invalid,
                        // try the next attempt.
                        continue;
                    }

                    if (result?.IsValid == true)
                    {
                        return lastCandidate;
                    }
                }
                else
                {
                    // Non-IEvent constructed leaf — no validation gate, take it.
                    return lastCandidate;
                }
            }

            // Exhausted MaxAttempts. Prefer the authored example; otherwise the
            // latest random attempt (which at least populates every leaf with
            // a type-correct value).
            return exampleSerialized ?? lastCandidate;
        }

        // ---- Construction ------------------------------------------------

        private static object? DeepClone(IEvent source, Type clrType)
        {
            try
            {
                var json = JsonConvert.SerializeObject(source);
                return JsonConvert.DeserializeObject(json, clrType);
            }
            catch
            {
                return null;
            }
        }

        private static object? CreateEvent(Type clrType)
        {
            if (clrType.IsAbstract || clrType.IsInterface) return null;
            try
            {
                return Activator.CreateInstance(clrType, nonPublic: true);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsComplex(Type type)
        {
            if (type is null) return false;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying.IsPrimitive) return false;
            if (underlying.IsEnum) return false;
            if (underlying == typeof(string)) return false;
            if (underlying == typeof(Guid)) return false;
            if (underlying == typeof(DateTime)) return false;
            if (underlying == typeof(DateTimeOffset)) return false;
            if (underlying == typeof(TimeSpan)) return false;
            if (underlying == typeof(decimal)) return false;
            return underlying.IsClass || (underlying.IsValueType && !underlying.IsPrimitive);
        }

        private static bool IsInstantiableComplex(Type type)
        {
            if (type is null) return false;
            if (type.IsAbstract || type.IsInterface) return false;
            if (!IsComplex(type)) return false;
            // Value types always have a parameterless constructor.
            if (type.IsValueType) return true;
            return type.GetConstructor(Type.EmptyTypes) is not null;
        }

        // ---- Randomization walker ---------------------------------------

        private static void Randomize(object instance, Random random, int depth, bool buildMissing)
        {
            if (instance is null) return;
            if (depth > MaxDepth) return;

            var type = instance.GetType();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite) continue;
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                if (string.Equals(prop.Name, "MessageMetadata", StringComparison.Ordinal)) continue;

                var propType = prop.PropertyType;

                // Recurse into collection elements (do NOT fabricate new ones).
                if (TryGetEnumerable(prop, instance, out var enumerable, out var elementType) && enumerable is not null)
                {
                    foreach (var element in enumerable)
                    {
                        if (element is null) continue;
                        if (!IsComplex(element.GetType())) continue;
                        Randomize(element, random, depth + 1, buildMissing);
                    }
                    continue;
                }

                if (IsComplex(propType))
                {
                    object? value;
                    try { value = prop.GetValue(instance); }
                    catch { continue; }

                    if (value is null && buildMissing && IsInstantiableComplex(propType))
                    {
                        try
                        {
                            value = Activator.CreateInstance(propType, nonPublic: true);
                            prop.SetValue(instance, value);
                        }
                        catch
                        {
                            value = null;
                        }
                    }

                    if (value is not null)
                    {
                        Randomize(value, random, depth + 1, buildMissing);
                    }
                    continue;
                }

                if (TryFakeLeaf(prop, random, out var leaf))
                {
                    try { prop.SetValue(instance, leaf); }
                    catch
                    {
                        // Type mismatch / converter rejection — skip; the
                        // attempt-level retry handles the broader fallback.
                    }
                }
            }
        }

        private static bool TryGetEnumerable(PropertyInfo prop, object instance, out IEnumerable? enumerable, out Type? elementType)
        {
            enumerable = null;
            elementType = null;

            var propType = prop.PropertyType;
            if (propType == typeof(string)) return false;
            if (!typeof(IEnumerable).IsAssignableFrom(propType)) return false;

            try
            {
                var value = prop.GetValue(instance);
                if (value is IEnumerable e)
                {
                    enumerable = e;
                    // Discover the element type from a generic interface.
                    elementType = propType.IsArray
                        ? propType.GetElementType()
                        : propType.GetInterfaces()
                            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                            ?.GetGenericArguments()[0];
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        // ---- Leaf-value strategy ---------------------------------------

        private static bool TryFakeLeaf(PropertyInfo prop, Random random, out object? value)
        {
            value = null;
            var propType = prop.PropertyType;
            var underlying = Nullable.GetUnderlyingType(propType) ?? propType;

            if (underlying.IsEnum)
            {
                var members = Enum.GetValues(underlying);
                if (members.Length == 0) return false;
                value = members.GetValue(random.Next(members.Length));
                return true;
            }
            if (underlying == typeof(Guid))
            {
                value = Guid.NewGuid();
                return true;
            }
            if (underlying == typeof(bool))
            {
                value = random.Next(2) == 1;
                return true;
            }
            if (underlying == typeof(DateTime))
            {
                value = DateTime.UtcNow.AddDays(-random.Next(0, 366));
                return true;
            }
            if (underlying == typeof(DateTimeOffset))
            {
                value = DateTimeOffset.UtcNow.AddDays(-random.Next(0, 366));
                return true;
            }
            if (underlying == typeof(int) || underlying == typeof(short) || underlying == typeof(long)
                || underlying == typeof(byte) || underlying == typeof(sbyte)
                || underlying == typeof(uint) || underlying == typeof(ushort) || underlying == typeof(ulong))
            {
                var n = random.Next(1, 1000);
                value = Convert.ChangeType(n, underlying, CultureInfo.InvariantCulture);
                return true;
            }
            if (underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float))
            {
                var n = Math.Round(random.NextDouble() * 1000.0, 2);
                value = Convert.ChangeType(n, underlying, CultureInfo.InvariantCulture);
                return true;
            }
            if (underlying == typeof(string))
            {
                value = FakeString(prop, random);
                return true;
            }

            return false;
        }

        private static string FakeString(PropertyInfo prop, Random random)
        {
            var heuristic = HeuristicString(prop.Name, random);
            return ClampToLength(heuristic, prop, random);
        }

        private static string HeuristicString(string name, Random random)
        {
            var lower = name?.ToLowerInvariant() ?? string.Empty;
            if (lower.EndsWith("email", StringComparison.Ordinal))
            {
                return $"{Pick(FirstNames, random).ToLowerInvariant()}.{Pick(LastNames, random).ToLowerInvariant()}@{Pick(EmailDomains, random)}";
            }
            if (lower.EndsWith("phone", StringComparison.Ordinal) || lower.EndsWith("phonenumber", StringComparison.Ordinal))
            {
                return $"{Pick(PhonePrefixes, random)} {RandomDigits(random, 8)}";
            }
            if (lower == "firstname" || lower == "givenname") return Pick(FirstNames, random);
            if (lower == "lastname" || lower == "surname" || lower == "familyname") return Pick(LastNames, random);
            if (lower == "fullname" || lower == "displayname" || lower == "name")
            {
                return $"{Pick(FirstNames, random)} {Pick(LastNames, random)}";
            }
            if (lower == "legalname" || lower == "companyname" || lower == "organizationname" || lower == "organisationname")
            {
                return $"{Pick(Companies, random)} {Pick(CompanySuffixes, random)}";
            }
            if (lower == "countrycode" || lower == "country") return Pick(Countries, random);
            if (lower == "taxid" || lower == "vatnumber" || lower == "vat") return $"TAX-{RandomDigits(random, 7)}";
            if (lower == "customernumber" || lower == "accountnumber" || lower == "ordernumber")
            {
                return RandomDigits(random, 6);
            }
            if (lower == "city") return Pick(Cities, random);
            if (lower.StartsWith("street", StringComparison.Ordinal) || lower.StartsWith("address", StringComparison.Ordinal))
            {
                return $"{Pick(Streets, random)} {random.Next(1, 200)}";
            }
            if (lower == "zipcode" || lower == "zip" || lower == "postalcode") return RandomDigits(random, 4);
            if (lower == "currency" || lower == "currencycode") return Pick(CurrencyCodes, random);
            if (lower.EndsWith("code", StringComparison.Ordinal)) return RandomDigits(random, 6);
            if (lower.EndsWith("id", StringComparison.Ordinal)) return Guid.NewGuid().ToString();

            return $"Sample {name} {random.Next(1, 1000)}";
        }

        private static string ClampToLength(string value, PropertyInfo prop, Random random)
        {
            if (value is null) value = string.Empty;

            int? min = null;
            int? max = null;

            var sl = prop.GetCustomAttribute<StringLengthAttribute>();
            if (sl is not null)
            {
                max = sl.MaximumLength;
                if (sl.MinimumLength > 0) min = sl.MinimumLength;
            }

            var minLen = prop.GetCustomAttribute<MinLengthAttribute>();
            if (minLen is not null && minLen.Length > (min ?? 0)) min = minLen.Length;

            var maxLen = prop.GetCustomAttribute<MaxLengthAttribute>();
            if (maxLen is not null)
            {
                if (max is null || maxLen.Length < max.Value) max = maxLen.Length;
            }

            if (max.HasValue && value.Length > max.Value)
            {
                value = value.Substring(0, Math.Max(0, max.Value));
            }
            if (min.HasValue && value.Length < min.Value)
            {
                // Pad with a-z to reach the minimum.
                var pad = new char[min.Value - value.Length];
                for (var i = 0; i < pad.Length; i++)
                {
                    pad[i] = (char)('a' + random.Next(26));
                }
                value += new string(pad);
                if (max.HasValue && value.Length > max.Value)
                {
                    value = value.Substring(0, max.Value);
                }
            }

            return value;
        }

        private static string Pick(string[] pool, Random random) => pool[random.Next(pool.Length)];

        private static string RandomDigits(Random random, int count)
        {
            var chars = new char[count];
            for (var i = 0; i < count; i++) chars[i] = (char)('0' + random.Next(10));
            return new string(chars);
        }
    }
}
