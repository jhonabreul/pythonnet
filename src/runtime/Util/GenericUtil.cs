using System.Linq;
using System;
using System.Collections.Generic;
using System.Resources;

namespace Python.Runtime
{
    /// <summary>
    /// This class is responsible for efficiently maintaining the bits
    /// of information we need to support aliases with 'nice names'.
    /// </summary>
    internal static class GenericUtil
    {
        /// <summary>
        /// Maps namespace -> generic base name -> list of generic type names
        /// </summary>
        private static Dictionary<string, Dictionary<string, List<string>>> mapping = new();

        public static void Reset()
        {
            mapping = new Dictionary<string, Dictionary<string, List<string>>>();
        }

        /// <summary>
        /// Register a generic type that appears in a given namespace.
        /// </summary>
        /// <param name="t">A generic type definition (<c>t.IsGenericTypeDefinition</c> must be true)</param>
        internal static void Register(Type t)
        {
            lock (mapping)
            {
                if (null == t.Namespace || null == t.Name)
                {
                    return;
                }

                Dictionary<string, List<string>> nsmap;
                if (!mapping.TryGetValue(t.Namespace, out nsmap))
                {
                    nsmap = new Dictionary<string, List<string>>();
                    mapping[t.Namespace] = nsmap;
                }

                string basename = GetBasename(t.Name);
                List<string> gnames;
                if (!nsmap.TryGetValue(basename, out gnames))
                {
                    gnames = new List<string>();
                    nsmap[basename] = gnames;
                }

                gnames.Add(t.Name);
            }
        }

        /// <summary>
        /// xxx
        /// </summary>
        public static List<string>? GetGenericBaseNames(string ns)
        {
            lock (mapping)
            {
                Dictionary<string, List<string>> nsmap;
                if (!mapping.TryGetValue(ns, out nsmap))
                {
                    return null;
                }
                var names = new List<string>();
                foreach (string key in nsmap.Keys)
                {
                    names.Add(key);
                }
                return names;
            }
        }

        /// <summary>
        /// Finds a generic type with the given number of generic parameters and the same name and namespace as <paramref name="t"/>.
        /// </summary>
        public static Type? GenericForType(Type t, int paramCount)
        {
            return GenericByName(t.Namespace, t.Name, paramCount);
        }

        /// <summary>
        /// Finds a generic type in the given namespace with the given name and number of generic parameters.
        /// </summary>
        public static Type? GenericByName(string ns, string basename, int paramCount)
        {
            lock (mapping)
            {
                Dictionary<string, List<string>> nsmap;
                if (!mapping.TryGetValue(ns, out nsmap))
                {
                    return null;
                }

                List<string> names;
                if (!nsmap.TryGetValue(GetBasename(basename), out names))
                {
                    return null;
                }

                foreach (string name in names)
                {
                    string qname = ns + "." + name;
                    Type o = AssemblyManager.LookupTypes(qname).FirstOrDefault();
                    if (o != null && o.GetGenericArguments().Length == paramCount)
                    {
                        return o;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// xxx
        /// </summary>
        public static string? GenericNameForBaseName(string ns, string name)
        {
            lock (mapping)
            {
                Dictionary<string, List<string>> nsmap;
                if (!mapping.TryGetValue(ns, out nsmap))
                {
                    return null;
                }

                List<string> gnames;
                nsmap.TryGetValue(name, out gnames);
                if (gnames?.Count > 0)
                {
                    return gnames[0];
                }
            }

            return null;
        }

        private static string GetBasename(string name)
        {
            int tick = name.IndexOf("`");
            if (tick > -1)
            {
                return name.Substring(0, tick);
            }
            else
            {
                return name;
            }
        }
    }
}
