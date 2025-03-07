using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Python.Runtime
{
    /// <summary>
    /// A MethodBinder encapsulates information about a (possibly overloaded)
    /// managed method, and is responsible for selecting the right method given
    /// a set of Python arguments. This is also used as a base class for the
    /// ConstructorBinder, a minor variation used to invoke constructors.
    /// </summary>
    [Serializable]
    internal class MethodBinder
    {
        [NonSerialized]
        private List<MethodInformation> list;
        [NonSerialized]
        private static Dictionary<string, MethodInfo> _resolvedGenericsCache = new();
        public const bool DefaultAllowThreads = true;
        public bool allow_threads = DefaultAllowThreads;
        public bool init = false;

        internal MethodBinder(List<MethodInformation> list)
        {
            this.list = list;
        }

        internal MethodBinder()
        {
            list = new List<MethodInformation>();
        }

        internal MethodBinder(MethodInfo mi)
        {
            list = new List<MethodInformation> { new MethodInformation(mi, true) };
        }

        public int Count
        {
            get { return list.Count; }
        }

        internal void AddMethod(MethodBase m, bool isOriginal)
        {
            // we added a new method so we have to re sort the method list
            init = false;
            list.Add(new MethodInformation(m, isOriginal));
        }

        /// <summary>
        /// Given a sequence of MethodInfo and a sequence of types, return the
        /// MethodInfo that matches the signature represented by those types.
        /// </summary>
        internal static MethodBase? MatchSignature(MethodBase[] mi, Type[] tp)
        {
            if (tp == null)
            {
                return null;
            }
            int count = tp.Length;
            foreach (MethodBase t in mi)
            {
                ParameterInfo[] pi = t.GetParameters();
                if (pi.Length != count)
                {
                    continue;
                }
                for (var n = 0; n < pi.Length; n++)
                {
                    if (tp[n] != pi[n].ParameterType)
                    {
                        break;
                    }
                    if (n == pi.Length - 1)
                    {
                        return t;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Given a sequence of MethodInfo and a sequence of type parameters,
        /// return the MethodInfo that represents the matching closed generic.
        /// </summary>
        internal static List<MethodInformation> MatchParameters(MethodBinder binder, Type[] tp)
        {
            if (tp == null)
            {
                return null;
            }
            int count = tp.Length;
            var result = new List<MethodInformation>(count);
            foreach (var methodInformation in binder.list)
            {
                var t = methodInformation.MethodBase;
                if (!t.IsGenericMethodDefinition)
                {
                    continue;
                }
                Type[] args = t.GetGenericArguments();
                if (args.Length != count)
                {
                    continue;
                }
                try
                {
                    // MakeGenericMethod can throw ArgumentException if the type parameters do not obey the constraints.
                    MethodInfo method = ((MethodInfo)t).MakeGenericMethod(tp);
                    Exceptions.Clear();
                    result.Add(new MethodInformation(method, methodInformation.IsOriginal));
                }
                catch (ArgumentException e)
                {
                    Exceptions.SetError(e);
                    // The error will remain set until cleared by a successful match.
                }
            }
            return result;
        }

        // Given a generic method and the argsTypes previously matched with it,
        // generate the matching method
        internal static MethodInfo ResolveGenericMethod(MethodInfo method, Object[] args)
        {
            // No need to resolve a method where generics are already assigned
            if (!method.ContainsGenericParameters)
            {
                return method;
            }

            bool shouldCache = method.DeclaringType != null;
            string key = null;

            // Check our resolved generics cache first
            if (shouldCache)
            {
                key = method.DeclaringType.AssemblyQualifiedName + method.ToString() + string.Join(",", args.Select(x => x?.GetType()));
                if (_resolvedGenericsCache.TryGetValue(key, out var cachedMethod))
                {
                    return cachedMethod;
                }
            }

            // Get our matching generic types to create our method
            var methodGenerics = method.GetGenericArguments().Where(x => x.IsGenericParameter).ToArray();
            var resolvedGenericsTypes = new Type[methodGenerics.Length];
            int resolvedGenerics = 0;

            var parameters = method.GetParameters();

            // Iterate to length of ArgTypes since default args are plausible
            for (int k = 0; k < args.Length; k++)
            {
                if (args[k] == null)
                {
                    continue;
                }

                var argType = args[k].GetType();
                var parameterType = parameters[k].ParameterType;

                // Ignore those without generic params
                if (!parameterType.ContainsGenericParameters)
                {
                    continue;
                }

                // The parameters generic definition
                var paramGenericDefinition = parameterType.GetGenericTypeDefinition();

                // For the arg that matches this param index, determine the matching type for the generic
                var currentType = argType;
                while (currentType != null)
                {

                    // Check the current type for generic type definition
                    var genericType = currentType.IsGenericType ? currentType.GetGenericTypeDefinition() : null;

                    // If the generic type matches our params generic definition, this is our match
                    // go ahead and match these types to this arg
                    if (paramGenericDefinition == genericType)
                    {

                        // The matching generic for this method parameter
                        var paramGenerics = parameterType.GenericTypeArguments;
                        var argGenericsResolved = currentType.GenericTypeArguments;

                        for (int j = 0; j < paramGenerics.Length; j++)
                        {

                            // Get the final matching index for our resolved types array for this params generic
                            var index = Array.IndexOf(methodGenerics, paramGenerics[j]);

                            if (resolvedGenericsTypes[index] == null)
                            {
                                // Add it, and increment our count
                                resolvedGenericsTypes[index] = argGenericsResolved[j];
                                resolvedGenerics++;
                            }
                            else if (resolvedGenericsTypes[index] != argGenericsResolved[j])
                            {
                                // If we have two resolved types for the same generic we have a problem
                                throw new ArgumentException("ResolveGenericMethod(): Generic method mismatch on argument types");
                            }
                        }

                        break;
                    }

                    // Step up the inheritance tree
                    currentType = currentType.BaseType;
                }
            }

            try
            {
                if (resolvedGenerics != methodGenerics.Length)
                {
                    throw new Exception($"ResolveGenericMethod(): Count of resolved generics {resolvedGenerics} does not match method generic count {methodGenerics.Length}.");
                }

                method = method.MakeGenericMethod(resolvedGenericsTypes);

                if (shouldCache)
                {
                    // Add to cache
                    _resolvedGenericsCache.Add(key, method);
                }
            }
            catch (ArgumentException e)
            {
                // Will throw argument exception if improperly matched
                Exceptions.SetError(e);
            }

            return method;
        }


        /// <summary>
        /// Given a sequence of MethodInfo and two sequences of type parameters,
        /// return the MethodInfo that matches the signature and the closed generic.
        /// </summary>
        internal static MethodInfo MatchSignatureAndParameters(MethodBase[] mi, Type[] genericTp, Type[] sigTp)
        {
            if (genericTp == null || sigTp == null)
            {
                return null;
            }
            int genericCount = genericTp.Length;
            int signatureCount = sigTp.Length;
            foreach (MethodInfo t in mi)
            {
                if (!t.IsGenericMethodDefinition)
                {
                    continue;
                }
                Type[] genericArgs = t.GetGenericArguments();
                if (genericArgs.Length != genericCount)
                {
                    continue;
                }
                ParameterInfo[] pi = t.GetParameters();
                if (pi.Length != signatureCount)
                {
                    continue;
                }
                for (var n = 0; n < pi.Length; n++)
                {
                    if (sigTp[n] != pi[n].ParameterType)
                    {
                        break;
                    }
                    if (n == pi.Length - 1)
                    {
                        MethodInfo match = t;
                        if (match.IsGenericMethodDefinition)
                        {
                            // FIXME: typeArgs not used
                            Type[] typeArgs = match.GetGenericArguments();
                            return match.MakeGenericMethod(genericTp);
                        }
                        return match;
                    }
                }
            }
            return null;
        }


        /// <summary>
        /// Return the array of MethodInfo for this method. The result array
        /// is arranged in order of precedence (done lazily to avoid doing it
        /// at all for methods that are never called).
        /// </summary>
        internal List<MethodInformation> GetMethods()
        {
            if (!init)
            {
                // I'm sure this could be made more efficient.
                list.Sort(new MethodSorter());
                init = true;
            }
            return list;
        }

        /// <summary>
        /// Precedence algorithm largely lifted from Jython - the concerns are
        /// generally the same so we'll start with this and tweak as necessary.
        /// </summary>
        /// <remarks>
        /// Based from Jython `org.python.core.ReflectedArgs.precedence`
        /// See: https://github.com/jythontools/jython/blob/master/src/org/python/core/ReflectedArgs.java#L192
        /// </remarks>
        private static int GetPrecedence(MethodInformation methodInformation)
        {
            return GetMatchedArgumentsPrecedence(methodInformation, null, null);
        }

        /// <summary>
        /// Gets the precedence of a method's arguments, considering only those arguments that have been matched,
        /// that is, those that are not default values.
        /// </summary>
        private static int GetMatchedArgumentsPrecedence(MethodInformation methodInformation, int? matchedPositionalArgsCount, IEnumerable<string> matchedKwargsNames)
        {
            ParameterInfo[] pi = methodInformation.ParameterInfo;
            var mi = methodInformation.MethodBase;
            int val = mi.IsStatic ? 3000 : 0;
            var isOperatorMethod = OperatorMethod.IsOperatorMethod(methodInformation.MethodBase);

            val += mi.IsGenericMethod ? 1 : 0;

            if (!matchedPositionalArgsCount.HasValue)
            {
                for (var i = 0; i < pi.Length; i++)
                {
                    val += ArgPrecedence(pi[i].ParameterType, isOperatorMethod);
                }
            }
            else
            {
                matchedKwargsNames ??= Array.Empty<string>();
                for (var i = 0; i < pi.Length; i++)
                {
                    if (i < matchedPositionalArgsCount || matchedKwargsNames.Contains(methodInformation.ParameterNames[i]))
                    {
                        val += ArgPrecedence(pi[i].ParameterType, isOperatorMethod);
                    }
                }
            }

            var info = mi as MethodInfo;
            if (info != null)
            {
                val += ArgPrecedence(info.ReturnType, isOperatorMethod);
                if (mi.DeclaringType == mi.ReflectedType)
                {
                    val += methodInformation.IsOriginal ? 0 : 300000;
                }
                else
                {
                    val += methodInformation.IsOriginal ? 2000 : 400000;
                }
            }

            return val;
        }

        /// <summary>
        /// Return a precedence value for a particular Type object.
        /// </summary>
        internal static int ArgPrecedence(Type t, bool isOperatorMethod)
        {
            Type objectType = typeof(object);
            if (t == objectType)
            {
                return 3000;
            }

            if (t.IsAssignableFrom(typeof(PyObject)) && !isOperatorMethod)
            {
                return -1;
            }

            if (t.IsArray)
            {
                Type e = t.GetElementType();
                if (e == objectType)
                {
                    return 2500;
                }
                return 100 + ArgPrecedence(e, isOperatorMethod);
            }

            TypeCode tc = Type.GetTypeCode(t);
            // TODO: Clean up
            switch (tc)
            {
                case TypeCode.Object:
                    return 1;

                // we place higher precision methods at the top
                case TypeCode.Decimal:
                    return 2;
                case TypeCode.Double:
                    return 3;
                case TypeCode.Single:
                    return 4;

                case TypeCode.Int64:
                    return 21;
                case TypeCode.Int32:
                    return 22;
                case TypeCode.Int16:
                    return 23;
                case TypeCode.UInt64:
                    return 24;
                case TypeCode.UInt32:
                    return 25;
                case TypeCode.UInt16:
                    return 26;
                case TypeCode.Char:
                    return 27;
                case TypeCode.Byte:
                    return 28;
                case TypeCode.SByte:
                    return 29;

                case TypeCode.String:
                    return 30;

                case TypeCode.Boolean:
                    return 40;
            }

            return 2000;
        }

        /// <summary>
        /// Bind the given Python instance and arguments to a particular method
        /// overload and return a structure that contains the converted Python
        /// instance, converted arguments and the correct method to call.
        /// </summary>
        internal Binding Bind(BorrowedReference inst, BorrowedReference args, BorrowedReference kw)
        {
            return Bind(inst, args, kw, null);
        }

        internal Binding Bind(BorrowedReference inst, BorrowedReference args, BorrowedReference kw, MethodBase info)
        {
            // If we have KWArgs create dictionary and collect them
            Dictionary<string, PyObject> kwArgDict = null;
            if (kw != null)
            {
                var pyKwArgsCount = (int)Runtime.PyDict_Size(kw);
                kwArgDict = new Dictionary<string, PyObject>(pyKwArgsCount);
                using var keylist = Runtime.PyDict_Keys(kw);
                using var valueList = Runtime.PyDict_Values(kw);
                for (int i = 0; i < pyKwArgsCount; ++i)
                {
                    var keyStr = Runtime.GetManagedString(Runtime.PyList_GetItem(keylist.Borrow(), i));
                    BorrowedReference value = Runtime.PyList_GetItem(valueList.Borrow(), i);
                    kwArgDict[keyStr!] = new PyObject(value);
                }
            }
            var hasNamedArgs = kwArgDict != null && kwArgDict.Count > 0;

            // Fetch our methods we are going to attempt to match and bind too.
            var methods = info == null ? GetMethods()
                : new List<MethodInformation>(1) { new MethodInformation(info, true) };

            int pyArgCount = (int)Runtime.PyTuple_Size(args);
            var matches = new List<MatchedMethod>(methods.Count);
            List<MatchedMethod> matchesUsingImplicitConversion = null;

            for (var i = 0; i < methods.Count; i++)
            {
                var methodInformation = methods[i];
                // Relevant method variables
                var mi = methodInformation.MethodBase;
                var pi = methodInformation.ParameterInfo;
                // Avoid accessing the parameter names property unless necessary
                var paramNames = hasNamedArgs ? methodInformation.ParameterNames : Array.Empty<string>();

                // Special case for operators
                bool isOperator = OperatorMethod.IsOperatorMethod(mi);
                // Binary operator methods will have 2 CLR args but only one Python arg
                // (unary operators will have 1 less each), since Python operator methods are bound.
                isOperator = isOperator && pyArgCount == pi.Length - 1;
                bool isReverse = isOperator && OperatorMethod.IsReverse((MethodInfo)mi);  // Only cast if isOperator.
                if (isReverse && OperatorMethod.IsComparisonOp((MethodInfo)mi))
                    continue;  // Comparison operators in Python have no reverse mode.
                // Preprocessing pi to remove either the first or second argument.
                if (isOperator && !isReverse)
                {
                    // The first Python arg is the right operand, while the bound instance is the left.
                    // We need to skip the first (left operand) CLR argument.
                    pi = pi.Skip(1).ToArray();
                }
                else if (isOperator && isReverse)
                {
                    // The first Python arg is the left operand.
                    // We need to take the first CLR argument.
                    pi = pi.Take(1).ToArray();
                }

                // Must be done after IsOperator section
                int clrArgCount = pi.Length;

                if (CheckMethodArgumentsMatch(clrArgCount,
                    pyArgCount,
                    kwArgDict,
                    pi,
                    paramNames,
                    out bool paramsArray,
                    out ArrayList defaultArgList))
                {
                    var outs = 0;
                    var margs = new object[clrArgCount];

                    int paramsArrayIndex = paramsArray ? pi.Length - 1 : -1; // -1 indicates no paramsArray
                    var usedImplicitConversion = false;
                    var kwargsMatched = 0;

                    // Conversion loop for each parameter
                    for (int paramIndex = 0; paramIndex < clrArgCount; paramIndex++)
                    {
                        PyObject tempPyObject = null;
                        BorrowedReference op = null;            // Python object to be converted; not yet set
                        var parameter = pi[paramIndex];     // Clr parameter we are targeting
                        object arg;                         // Python -> Clr argument

                        // Check positional arguments first and then check for named arguments and optional values
                        if (paramIndex >= pyArgCount)
                        {
                            var hasNamedParam = kwArgDict == null ? false : kwArgDict.TryGetValue(paramNames[paramIndex], out tempPyObject);

                            // All positional arguments have been used:
                            // Check our KWargs for this parameter
                            if (hasNamedParam)
                            {
                                kwargsMatched++;
                                if (tempPyObject != null)
                                {
                                    op = tempPyObject;
                                }
                            }
                            else if (parameter.IsOptional && !(hasNamedParam || (paramsArray && paramIndex == paramsArrayIndex)))
                            {
                                if (defaultArgList != null)
                                {
                                    margs[paramIndex] = defaultArgList[paramIndex - pyArgCount];
                                }

                                continue;
                            }
                        }

                        NewReference tempObject = default;

                        // At this point, if op is IntPtr.Zero we don't have a KWArg and are not using default
                        if (op == null)
                        {
                            // If we have reached the paramIndex
                            if (paramsArrayIndex == paramIndex)
                            {
                                op = HandleParamsArray(args, paramsArrayIndex, pyArgCount, out tempObject);
                            }
                            else
                            {
                                op = Runtime.PyTuple_GetItem(args, paramIndex);
                            }
                        }

                        // this logic below handles cases when multiple overloading methods
                        // are ambiguous, hence comparison between Python and CLR types
                        // is necessary
                        Type clrtype = null;
                        NewReference pyoptype = default;
                        if (methods.Count > 1)
                        {
                            pyoptype = Runtime.PyObject_Type(op);
                            Exceptions.Clear();
                            if (!pyoptype.IsNull())
                            {
                                clrtype = Converter.GetTypeByAlias(pyoptype.Borrow());
                            }
                            pyoptype.Dispose();
                        }


                        if (clrtype != null)
                        {
                            var typematch = false;

                            if ((parameter.ParameterType != typeof(object)) && (parameter.ParameterType != clrtype))
                            {
                                var pytype = Converter.GetPythonTypeByAlias(parameter.ParameterType);
                                pyoptype = Runtime.PyObject_Type(op);
                                Exceptions.Clear();
                                if (!pyoptype.IsNull())
                                {
                                    if (pytype != pyoptype.Borrow())
                                    {
                                        typematch = false;
                                    }
                                    else
                                    {
                                        typematch = true;
                                        clrtype = parameter.ParameterType;
                                    }
                                }
                                if (!typematch)
                                {
                                    // this takes care of nullables
                                    var underlyingType = Nullable.GetUnderlyingType(parameter.ParameterType);
                                    if (underlyingType == null)
                                    {
                                        underlyingType = parameter.ParameterType;
                                    }
                                    // this takes care of enum values
                                    TypeCode argtypecode = Type.GetTypeCode(underlyingType);
                                    TypeCode paramtypecode = Type.GetTypeCode(clrtype);
                                    if (argtypecode == paramtypecode)
                                    {
                                        typematch = true;
                                        clrtype = parameter.ParameterType;
                                    }
                                    // we won't take matches using implicit conversions if there is already a match
                                    // not using implicit conversions
                                    else if (matches.Count == 0)
                                    {
                                        // accepts non-decimal numbers in decimal parameters
                                        if (underlyingType == typeof(decimal))
                                        {
                                            clrtype = parameter.ParameterType;
                                            usedImplicitConversion |= typematch = Converter.ToManaged(op, clrtype, out arg, false);
                                        }
                                        if (!typematch)
                                        {
                                            // this takes care of implicit conversions
                                            var opImplicit = parameter.ParameterType.GetMethod("op_Implicit", new[] { clrtype });
                                            if (opImplicit != null)
                                            {
                                                usedImplicitConversion |= typematch = opImplicit.ReturnType == parameter.ParameterType;
                                                clrtype = parameter.ParameterType;
                                            }
                                        }
                                    }
                                }
                                pyoptype.Dispose();
                                if (!typematch)
                                {
                                    tempObject.Dispose();
                                    margs = null;
                                    break;
                                }
                            }
                            else
                            {
                                clrtype = parameter.ParameterType;
                            }
                        }
                        else
                        {
                            clrtype = parameter.ParameterType;
                        }

                        if (parameter.IsOut || clrtype.IsByRef)
                        {
                            outs++;
                        }

                        if (!Converter.ToManaged(op, clrtype, out arg, false))
                        {
                            tempObject.Dispose();
                            margs = null;
                            break;
                        }
                        tempObject.Dispose();

                        margs[paramIndex] = arg;

                    }

                    if (margs == null)
                    {
                        continue;
                    }

                    if (isOperator)
                    {
                        if (inst != null)
                        {
                            if (ManagedType.GetManagedObject(inst) is CLRObject co)
                            {
                                bool isUnary = pyArgCount == 0;
                                // Postprocessing to extend margs.
                                var margsTemp = isUnary ? new object[1] : new object[2];
                                // If reverse, the bound instance is the right operand.
                                int boundOperandIndex = isReverse ? 1 : 0;
                                // If reverse, the passed instance is the left operand.
                                int passedOperandIndex = isReverse ? 0 : 1;
                                margsTemp[boundOperandIndex] = co.inst;
                                if (!isUnary)
                                {
                                    margsTemp[passedOperandIndex] = margs[0];
                                }
                                margs = margsTemp;
                            }
                            else continue;
                        }
                    }

                    var match = new MatchedMethod(kwargsMatched, margs, outs, methodInformation);
                    if (usedImplicitConversion)
                    {
                        if (matchesUsingImplicitConversion == null)
                        {
                            matchesUsingImplicitConversion = new List<MatchedMethod>();
                        }
                        matchesUsingImplicitConversion.Add(match);
                    }
                    else
                    {
                        matches.Add(match);
                        // We don't need the matches using implicit conversion anymore, we can free the memory
                        matchesUsingImplicitConversion = null;
                    }
                }
            }

            if (matches.Count > 0 || (matchesUsingImplicitConversion != null && matchesUsingImplicitConversion.Count > 0))
            {
                // We favor matches that do not use implicit conversion
                var matchesTouse = matches.Count > 0 ? matches : matchesUsingImplicitConversion;

                // The best match would be the one with the most named arguments matched.
                // But if multiple matches have the same max number of named arguments matched,
                // we solve the ambiguity by taking the one with the highest precedence but only
                // considering the actual arguments passed, ignoring the optional arguments for
                // which the default values were used
                var bestMatch = matchesTouse
                    .GroupBy(x => x.KwargsMatched)
                    .OrderByDescending(x => x.Key)
                    .First()
                    .MinBy(x => GetMatchedArgumentsPrecedence(x.MethodInformation, pyArgCount, kwArgDict?.Keys));

                var margs = bestMatch.ManagedArgs;
                var outs = bestMatch.Outs;
                var mi = bestMatch.Method;

                object? target = null;
                if (!mi.IsStatic && inst != null)
                {
                    //CLRObject co = (CLRObject)ManagedType.GetManagedObject(inst);
                    // InvalidCastException: Unable to cast object of type
                    // 'Python.Runtime.ClassObject' to type 'Python.Runtime.CLRObject'

                    // Sanity check: this ensures a graceful exit if someone does
                    // something intentionally wrong like call a non-static method
                    // on the class rather than on an instance of the class.
                    // XXX maybe better to do this before all the other rigmarole.
                    if (ManagedType.GetManagedObject(inst) is CLRObject co)
                    {
                        target = co.inst;
                    }
                    else
                    {
                        Exceptions.SetError(Exceptions.TypeError, "Invoked a non-static method with an invalid instance");
                        return null;
                    }
                }

                // If this match is generic we need to resolve it with our types.
                // Store this generic match to be used if no others match
                if (mi.IsGenericMethod)
                {
                    mi = ResolveGenericMethod((MethodInfo)mi, margs);
                }

                return new Binding(mi, target, margs, outs);
            }

            return null;
        }

        static BorrowedReference HandleParamsArray(BorrowedReference args, int arrayStart, int pyArgCount, out NewReference tempObject)
        {
            tempObject = default;
            // for a params method, we may have a sequence or single/multiple items
            // here we look to see if the item at the paramIndex is there or not
            // and then if it is a sequence itself.
            if ((pyArgCount - arrayStart) == 1)
            {
                // we only have one argument left, so we need to check it
                // to see if it is a sequence or a single item
                BorrowedReference item = Runtime.PyTuple_GetItem(args, arrayStart);
                if (!Runtime.PyString_Check(item) && (Runtime.PySequence_Check(item) || (ManagedType.GetManagedObject(item) as CLRObject)?.inst is IEnumerable))
                {
                    // it's a sequence (and not a string), so we use it as the op
                    return item;
                }
                else
                {
                    tempObject = Runtime.PyTuple_GetSlice(args, arrayStart, pyArgCount);
                    return tempObject.Borrow();
                }
            }
            else
            {
                tempObject = Runtime.PyTuple_GetSlice(args, arrayStart, pyArgCount);
                return tempObject.Borrow();
            }
        }

        /// <summary>
        /// This helper method will perform an initial check to determine if we found a matching
        /// method based on its parameters count and type <see cref="Bind(IntPtr,IntPtr,IntPtr,MethodBase,MethodInfo[])"/>
        /// </summary>
        /// <remarks>
        /// We required both the parameters info and the parameters names to perform this check.
        /// The CLR method parameters info is required to match the parameters count and type.
        /// The names are required to perform an accurate match, since the method can be the snake-cased version.
        /// </remarks>
        private bool CheckMethodArgumentsMatch(int clrArgCount,
            int pyArgCount,
            Dictionary<string, PyObject> kwargDict,
            ParameterInfo[] parameterInfo,
            string[] parameterNames,
            out bool paramsArray,
            out ArrayList defaultArgList)
        {
            var match = false;

            // Prepare our outputs
            defaultArgList = null;
            paramsArray = false;
            if (parameterInfo.Length > 0)
            {
                var lastParameterInfo = parameterInfo[parameterInfo.Length - 1];
                if (lastParameterInfo.ParameterType.IsArray)
                {
                    paramsArray = Attribute.IsDefined(lastParameterInfo, typeof(ParamArrayAttribute));
                }
            }

            // First if we have anys kwargs, look at the function for matching args
            if (kwargDict != null && kwargDict.Count > 0)
            {
                // If the method doesn't have all of these kw args, it is not a match
                // Otherwise just continue on to see if it is a match
                if (!kwargDict.All(x => parameterNames.Any(paramName => x.Key == paramName)))
                {
                    return false;
                }
            }

            // If they have the exact same amount of args they do match
            // Must check kwargs because it contains additional args
            if (pyArgCount == clrArgCount && (kwargDict == null || kwargDict.Count == 0))
            {
                match = true;
            }
            else if (pyArgCount < clrArgCount)
            {
                // every parameter past 'pyArgCount' must have either
                // a corresponding keyword argument or a default parameter
                match = true;
                defaultArgList = new ArrayList();
                for (var v = pyArgCount; v < clrArgCount && match; v++)
                {
                    if (kwargDict != null && kwargDict.ContainsKey(parameterNames[v]))
                    {
                        // we have a keyword argument for this parameter,
                        // no need to check for a default parameter, but put a null
                        // placeholder in defaultArgList
                        defaultArgList.Add(null);
                    }
                    else if (parameterInfo[v].IsOptional)
                    {
                        // IsOptional will be true if the parameter has a default value,
                        // or if the parameter has the [Optional] attribute specified.
                        if (parameterInfo[v].HasDefaultValue)
                        {
                            defaultArgList.Add(parameterInfo[v].DefaultValue);
                        }
                        else
                        {
                            // [OptionalAttribute] was specified for the parameter.
                            // See https://stackoverflow.com/questions/3416216/optionalattribute-parameters-default-value
                            // for rules on determining the value to pass to the parameter
                            var type = parameterInfo[v].ParameterType;
                            if (type == typeof(object))
                                defaultArgList.Add(Type.Missing);
                            else if (type.IsValueType)
                                defaultArgList.Add(Activator.CreateInstance(type));
                            else
                                defaultArgList.Add(null);
                        }
                    }
                    else if (!paramsArray)
                    {
                        // If there is no KWArg or Default value, then this isn't a match
                        match = false;
                    }
                }
            }
            else if (pyArgCount > clrArgCount && clrArgCount > 0 && paramsArray)
            {
                // This is a `foo(params object[] bar)` style method
                // We will handle the params later
                match = true;
            }
            return match;
        }

        internal virtual NewReference Invoke(BorrowedReference inst, BorrowedReference args, BorrowedReference kw)
        {
            return Invoke(inst, args, kw, null, null);
        }

        internal virtual NewReference Invoke(BorrowedReference inst, BorrowedReference args, BorrowedReference kw, MethodBase info)
        {
            return Invoke(inst, args, kw, info, null);
        }

        internal virtual NewReference Invoke(BorrowedReference inst, BorrowedReference args, BorrowedReference kw, MethodBase info, MethodInfo[] methodinfo)
        {
            Binding binding = Bind(inst, args, kw, info);
            object result;
            IntPtr ts = IntPtr.Zero;

            if (binding == null)
            {
                // If we already have an exception pending, don't create a new one
                if (!Exceptions.ErrorOccurred())
                {
                    var value = new StringBuilder("No method matches given arguments");
                    if (methodinfo != null && methodinfo.Length > 0)
                    {
                        value.Append($" for {methodinfo[0].Name}");
                    }
                    else if (list.Count > 0)
                    {
                        value.Append($" for {list[0].MethodBase.Name}");
                    }

                    value.Append(": ");
                    AppendArgumentTypes(to: value, args);
                    Exceptions.RaiseTypeError(value.ToString());
                }

                return default;
            }

            if (allow_threads)
            {
                ts = PythonEngine.BeginAllowThreads();
            }

            try
            {
                result = binding.info.Invoke(binding.inst, BindingFlags.Default, null, binding.args, null);
            }
            catch (Exception e)
            {
                if (e.InnerException != null)
                {
                    e = e.InnerException;
                }
                if (allow_threads)
                {
                    PythonEngine.EndAllowThreads(ts);
                }
                Exceptions.SetError(e);
                return default;
            }

            if (allow_threads)
            {
                PythonEngine.EndAllowThreads(ts);
            }

            // If there are out parameters, we return a tuple containing
            // the result followed by the out parameters. If there is only
            // one out parameter and the return type of the method is void,
            // we return the out parameter as the result to Python (for
            // code compatibility with ironpython).

            var returnType = binding.info.IsConstructor ? typeof(void) : ((MethodInfo)binding.info).ReturnType;

            if (binding.outs > 0)
            {
                ParameterInfo[] pi = binding.info.GetParameters();
                int c = pi.Length;
                var n = 0;

                bool isVoid = returnType == typeof(void);
                int tupleSize = binding.outs + (isVoid ? 0 : 1);
                using var t = Runtime.PyTuple_New(tupleSize);
                if (!isVoid)
                {
                    using var v = Converter.ToPython(result, returnType);
                    Runtime.PyTuple_SetItem(t.Borrow(), n, v.Steal());
                    n++;
                }

                for (var i = 0; i < c; i++)
                {
                    Type pt = pi[i].ParameterType;
                    if (pt.IsByRef)
                    {
                        using var v = Converter.ToPython(binding.args[i], pt.GetElementType());
                        Runtime.PyTuple_SetItem(t.Borrow(), n, v.Steal());
                        n++;
                    }
                }

                if (binding.outs == 1 && returnType == typeof(void))
                {
                    BorrowedReference item = Runtime.PyTuple_GetItem(t.Borrow(), 0);
                    return new NewReference(item);
                }

                return new NewReference(t.Borrow());
            }

            return Converter.ToPython(result, returnType);
        }

        /// <summary>
        /// Utility class to store the information about a <see cref="MethodBase"/>
        /// </summary>
        [Serializable]
        internal class MethodInformation
        {
            private ParameterInfo[] _parameterInfo;
            private string[] _parametersNames;

            public MethodBase MethodBase { get; }

            public bool IsOriginal { get; set; }

            public ParameterInfo[] ParameterInfo
            {
                get
                {
                    _parameterInfo ??= MethodBase.GetParameters();
                    return _parameterInfo;
                }
            }

            public string[] ParameterNames
            {
                get
                {
                    if (_parametersNames == null)
                    {
                        if (IsOriginal)
                        {
                            _parametersNames = ParameterInfo.Select(pi => pi.Name).ToArray();
                        }
                        else
                        {
                            _parametersNames = ParameterInfo.Select(pi => pi.Name.ToSnakeCase()).ToArray();
                        }
                    }
                    return _parametersNames;
                }
            }

            public MethodInformation(MethodBase methodBase, bool isOriginal)
            {
                MethodBase = methodBase;
                IsOriginal = isOriginal;
            }

            public override string ToString()
            {
                return MethodBase.ToString();
            }
        }

        /// <summary>
        /// Utility class to sort method info by parameter type precedence.
        /// </summary>
        private class MethodSorter : IComparer<MethodInformation>
        {
            public int Compare(MethodInformation x, MethodInformation y)
            {
                int p1 = GetPrecedence(x);
                int p2 = GetPrecedence(y);
                if (p1 < p2)
                {
                    return -1;
                }
                if (p1 > p2)
                {
                    return 1;
                }
                return 0;
            }
        }

        private readonly struct MatchedMethod
        {
            public int KwargsMatched { get; }
            public object?[] ManagedArgs { get; }
            public int Outs { get; }
            public MethodInformation MethodInformation { get; }
            public MethodBase Method => MethodInformation.MethodBase;

            public MatchedMethod(int kwargsMatched, object?[] margs, int outs, MethodInformation methodInformation)
            {
                KwargsMatched = kwargsMatched;
                ManagedArgs = margs;
                Outs = outs;
                MethodInformation = methodInformation;
            }
        }

        protected static void AppendArgumentTypes(StringBuilder to, BorrowedReference args)
        {
            long argCount = Runtime.PyTuple_Size(args);
            to.Append("(");
            for (nint argIndex = 0; argIndex < argCount; argIndex++)
            {
                BorrowedReference arg = Runtime.PyTuple_GetItem(args, argIndex);
                if (arg != null)
                {
                    BorrowedReference type = Runtime.PyObject_TYPE(arg);
                    if (type != null)
                    {
                        using var description = Runtime.PyObject_Str(type);
                        if (description.IsNull())
                        {
                            Exceptions.Clear();
                            to.Append(Util.BadStr);
                        }
                        else
                        {
                            to.Append(Runtime.GetManagedString(description.Borrow()));
                        }
                    }
                }

                if (argIndex + 1 < argCount)
                    to.Append(", ");
            }
            to.Append(')');
        }
    }


    /// <summary>
    /// A Binding is a utility instance that bundles together a MethodInfo
    /// representing a method to call, a (possibly null) target instance for
    /// the call, and the arguments for the call (all as managed values).
    /// </summary>
    internal class Binding
    {
        public MethodBase info;
        public object[] args;
        public object inst;
        public int outs;

        internal Binding(MethodBase info, object inst, object[] args, int outs)
        {
            this.info = info;
            this.inst = inst;
            this.args = args;
            this.outs = outs;
        }
    }
}
