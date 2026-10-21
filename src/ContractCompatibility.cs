using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Unpacker;

/// <summary>
/// 把混淆后残破的 IL 元数据修复成"C# 编译器能接受的形状"。
/// Eazfuscator 会改掉成员名和标志位，让反编译结果语法正确但语义编译不过：
///   1. 显式接口实现必须是"接口.成员"——ILSpy 只认"名字里有点 + 有 .override"
///      的实现，混淆后名字里没点，全掉成普通私有成员，于是 CS0737 / CS0535；
///   2. 一个属性被拆成 getter/setter 两个 PropertyDef，或访问器挂在别的属性
///      名下，与接口契约形状不一致，掉 CS0535；
///   3. 抽象基类成员的实现靠"同名同签名"才认得出，名字对不上掉 CS0534；
///   4. 被重写的基类方法丢了 virtual/final 标志，掉 CS0506 / CS0115 / CS0239；
///   5. 可访问性不一致（CS0050/51/52/53/58/60/61/CS0507）；
///   6. 编译器私有特性 IsReadOnlyAttribute / TupleElementNamesAttribute 出现在
///      源码里，掉 CS8335 / CS8141。
/// </summary>
internal static class ContractCompatibility
{
    private static readonly SigComparer Signature = new(SigComparerOptions.DontCompareTypeScope);

    private static class Visibility
    {
        public const int Private = 0;
        public const int Assembly = 1;
        public const int Protected = 2;
        public const int ProtectedInternal = 3;
        public const int Public = 4;
    }

    internal static void Normalize(ModuleDef module, TypeDef[] types, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods, Dictionary<PropertyDef, string> originalProperties)
    {
        var own = new HashSet<TypeDef>(types);
        SyncContracts(types, own, changes, originalMethods, originalProperties);
        StripMismatchedEventDefinitions(types);
        EnsureDefaultMemberAttributes(module, types);
        AlignInheritedMembers(types, own, changes, originalMethods);
        AlignImplicitOverrides(types, own, changes, originalMethods);
        AlignOrphanEventAccessors(types, own, changes, originalMethods);
        RepairOverrides(types, own, changes, originalMethods);
        AlignPropertyNames(types, own, changes);
        // 属性改名后 DefaultMember 指向的名字可能过期，索引器会认不出来（CS0535）。
        EnsureDefaultMemberAttributes(module, types);
        NormalizeAccessibility(module, types);
        StripCompilerReservedAttributes(module, types);
    }

    private static ICustomAttributeType? defaultMemberConstructor;

    /// <summary>
    /// 索引器要被反编译成 this[...]，类型上得有 [DefaultMember("索引器名")]，
    /// 否则 ILSpy 认为参数化属性"没有 C# 语法"，按普通方法输出——接口成员
    /// this[int] 就变成"没实现"（CS0535）。
    /// </summary>
    private static void EnsureDefaultMemberAttributes(ModuleDef module, TypeDef[] types)
    {
        foreach (var type in types)
        {
            var indexer = type.Properties.FirstOrDefault(p => p.PropertySig?.Params.Count > 0);
            if (indexer == null) continue;
            string name = indexer.Name.String;
            foreach (var attribute in type.CustomAttributes
                .Where(a => a.TypeFullName == "System.Reflection.DefaultMemberAttribute").ToArray())
            {
                if (attribute.ConstructorArguments.Count != 1) continue;
                if (attribute.ConstructorArguments[0].Value?.ToString() != name)
                    attribute.ConstructorArguments[0] =
                        new CAArgument(module.CorLibTypes.String, new UTF8String(name));
                return;
            }
            if (defaultMemberConstructor == null)
            {
                foreach (var candidate in types)
                    foreach (var attribute in candidate.CustomAttributes)
                        if (attribute.TypeFullName == "System.Reflection.DefaultMemberAttribute"
                            && attribute.Constructor != null)
                        {
                            defaultMemberConstructor = attribute.Constructor;
                            break;
                        }
                if (defaultMemberConstructor == null) return;
            }
            type.CustomAttributes.Add(new CustomAttribute((IMethodDefOrRef)defaultMemberConstructor,
                new[] { new CAArgument(module.CorLibTypes.String, new UTF8String(name)) }));
        }
    }

    /// <summary>
    /// 抽象基类成员的实现（名字被抹掉后只能靠签名认出来）对齐到基类的名字，
    /// 否则 C# 报 CS0534。只处理"派生类里没有同名成员、且签名唯一匹配"的情况，
    /// 命中不了就宁可不改，避免把无关成员改名。
    /// </summary>
    private static void AlignInheritedMembers(TypeDef[] types, HashSet<TypeDef> own, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods)
    {
        foreach (var type in types)
        {
            if (type.IsInterface) continue;

            // .override 行是权威的重写关系：实现方法必须和被重写成员同名，
            // 否则 C# 认不出这是重写（CS0506/CS0534）。被重写成员自己也可能
            // 在别处被改名，名字要沿链路传播，跑到不动点为止。
            for (int round = 0; round < 4; round++)
            {
                bool renamed = false;
                foreach (var method in type.Methods.ToArray())
                    foreach (var ov in method.Overrides)
                    {
                        var declaration = ov.MethodDeclaration.ResolveMethodDef();
                        if (declaration?.DeclaringType == null || !own.Contains(declaration.DeclaringType)) continue;
                        if ((declaration.SemanticsAttributes & MethodSemanticsAttributes.Getter) != 0
                            || (declaration.SemanticsAttributes & MethodSemanticsAttributes.Setter) != 0) continue;
                        if (declaration.DeclaringType.IsInterface) continue;
                        if (method.Name.String != declaration.Name.String)
                        {
                            ForceName(method, declaration.Name.String, changes, originalMethods);
                            renamed = true;
                        }
                    }
                if (!renamed) break;
            }

            if (type.IsAbstract) continue;
            var chain = BaseChain(type, own).ToArray();
            // 基类链上同签名的抽象成员（含派生接口/抽象类里的重声明）要共用一个
            // 名字：规范名字取最顶层的最初声明（NewSlot）那个。但改名只改"没人
            // 按现名字实现"的成员——已经用现名字对齐过的（混淆器的分组命名恰好
            // 把整条链统一过）不能动，动了反而和实现撞名（CS0534）。
            var abstractMembers = chain.SelectMany(entry => entry.Definition.Methods.Select(m => (Base: entry, Member: m)))
                .Where(pair => pair.Member.IsAbstract && !pair.Member.IsConstructor).ToArray();
            var handled = new HashSet<MethodDef>();
            foreach (var (baseType, member) in abstractMembers)
            {
                if (!handled.Add(member)) continue;
                var closed = Close(member.MethodSig, baseType.Arguments);
                var claimants = abstractMembers
                    .Where(pair => Signature.Equals(Close(pair.Member.MethodSig, pair.Base.Arguments), closed))
                    .Select(pair => pair.Member).ToArray();
                foreach (var claimant in claimants) handled.Add(claimant);
                string target = (claimants.LastOrDefault(m => m.IsNewSlot) ?? claimants[claimants.Length - 1]).Name.String;
                string originalName = member.Name.String;

                // 先定基类成员的最终名字，再让实现跟着它——顺序反了会把已经对齐
                // 好的实现改名改坏（CS0534）。
                foreach (var claimant in claimants)
                {
                    if (claimant.Name.String == target) continue;
                    // 已经有派生类按现名字实现它：这个名字是活的，不能改。
                    if (ImplementedElsewhere(claimant, types, own)) continue;
                    // 改了会和同类型里同名同签名的成员撞车：也不改（留给去重）。
                    if (claimant.DeclaringType.Methods.Any(m => m != claimant && m.Name.String == target
                        && Signature.Equals(m.MethodSig, claimant.MethodSig))) continue;
                    ForceName(claimant, target, changes, originalMethods);
                }
                string finalName = member.Name.String;

                // 实现：先按成员最终名字找，再按它原来的名字找（成员刚被改过名），
                // 最后才按签名兜底（唯一匹配）。基类里同签名的具体虚成员不要求
                // 实现，签名猜错了会把实现改名成别的成员（CS0534）。
                var body = type.Methods.FirstOrDefault(m => m.IsVirtual && m.Name.String == finalName
                    && Signature.Equals(m.MethodSig, closed))
                    ?? type.Methods.FirstOrDefault(m => m.IsVirtual && m.Name.String == originalName
                        && Signature.Equals(m.MethodSig, closed));
                if (body == null && claimants.Length == 1)
                {
                    var candidates = type.Methods.Where(m => m.IsVirtual && m.Overrides.Count == 0
                        && Signature.Equals(m.MethodSig, closed)).ToArray();
                    if (candidates.Length == 1) body = candidates[0];
                }
                if (body != null)
                {
                    if (body.Name.String != finalName)
                        ForceName(body, finalName, changes, originalMethods);
                    // 实现抽象成员是 override，必须 ReuseSlot：带 NewSlot 会被 C#
                    // 当成"隐藏"而不是"实现"（CS0533）。接口成员不是重写、struct 不能
                    // override，这两种情况清了 NewSlot 只会反编译出非法的 virtual。
                    if (!type.IsValueType && !member.DeclaringType.IsInterface)
                        body.IsNewSlot = false;
                    // 抽象成员的实现必须公开（或 protected internal）
                    body.Attributes = body.Attributes & ~MethodAttributes.MemberAccessMask
                        | ((member.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Family ? MethodAttributes.Family : MethodAttributes.Public);
                    if (finalName.StartsWith("get_"))
                    {
                        string propertyName = finalName.Substring(4);
                        if (!type.Properties.Any(p => p.Name.String == propertyName))
                        {
                            var signature = body.MethodSig;
                            var property = new PropertyDefUser(propertyName,
                                new PropertySig(signature.HasThis, signature.RetType, signature.Params.ToArray()))
                                { GetMethod = body };
                            body.IsSpecialName = true;
                            type.Properties.Add(property);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 是否有别的类型（成员声明类型的派生类）已经按这个名字实现了该成员。
    /// 改名只能改"没人认领"的成员，否则会把对齐好的链路又打乱（CS0534）。
    /// </summary>
    private static bool ImplementedElsewhere(MethodDef member, TypeDef[] types, HashSet<TypeDef> own)
    {
        var owner = member.DeclaringType;
        if (owner == null) return true;
        foreach (var type in types)
        {
            if (type == owner || type.IsInterface) continue;
            if (!BaseChain(type, own).Any(entry => entry.Definition == owner)) continue;
            foreach (var m in type.Methods)
                if (m.IsVirtual && m.Name.String == member.Name.String
                    && Signature.Equals(m.MethodSig, member.MethodSig))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// 隐式重写（ReuseSlot，没有 .override 行）在 IL 里靠"同名同签名"生效。混淆器
    /// 把实现改名后，C# 反编译成 override 却找不到被重写成员——CS0115。这里按签名
    /// 在基类链上找唯一的虚成员，把实现改回它的名字；有歧义或会撞名就不动。
    /// </summary>
    private static void AlignImplicitOverrides(TypeDef[] types, HashSet<TypeDef> own, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods)
    {
        foreach (var type in types)
        {
            if (type.IsInterface) continue;
            var chain = BaseChain(type, own).ToArray();
            if (chain.Length == 0) continue;
            foreach (var method in type.Methods.ToArray())
            {
                if (method.IsConstructor || method.IsStatic || !method.IsVirtual || method.IsNewSlot) continue;
                if (method.Name.String.Contains('.')) continue;
                // 基类链上已经有同名同签名的虚成员：这本来就是正确的重写，不能动。
                if (chain.Any(entry => entry.Definition.Methods.Any(m => m.IsVirtual
                        && m.Name.String == method.Name.String
                        && Signature.Equals(Close(m.MethodSig, entry.Arguments), method.MethodSig)))) continue;
                // 访问器只跟同类访问器比：普通方法不该被对齐到某个属性 getter 上。
                // 不能只看 SpecialName——混淆器会把基类 setter 的 SpecialName 抹掉，
                // 那样 get_X 和 set_X 就配不上对了，按名字前缀判断才稳。
                string prefix = AccessorPrefix(method.Name.String);
                var matches = chain
                    .SelectMany(entry => entry.Definition.Methods
                        .Where(m => m.IsVirtual && !m.IsStatic && m.Name.String != method.Name.String
                            && AccessorPrefix(m.Name.String) == prefix
                            && Signature.Equals(Close(m.MethodSig, entry.Arguments), method.MethodSig))
                        .Select(m => m))
                    .ToArray();
                if (matches.Select(m => m.Name.String).Distinct().Count() != 1) continue;
                var target = matches[0];
                // 本类型里已经有同名同签名的成员：改名会撞车，宁可不改。
                if (type.Methods.Any(m => m != method && m.Name.String == target.Name.String
                    && Signature.Equals(m.MethodSig, method.MethodSig))) continue;
                // private virtual 不是重写（CS0621），可访问性也必须和被重写成员一致
                // （CS0507）——对齐时一起改掉。
                if (method.IsPrivate) continue;
                ForceName(method, target.Name.String, changes, originalMethods);
                Align(method, target);
            }
        }
    }

    /// <summary>
    /// 事件的 add_/remove_ 是一对同签名的特殊方法。混淆器把基类那一对的名字也
    /// 抹成 method_XXXXX 之后，派生类里的 add_X/remove_X 就找不到重写目标
    /// （CS0115），基类那个抽象成员也没人实现（CS0534/CS0535）。名字既然对不上，
    /// 只能按声明顺序配对：两边同签名的方法各按 rid 排序后一一对应。
    /// 配对完把名字换成基类的（否则 C# 永远认为没实现），并顺手拆掉那个已经
    /// 名不副实的事件定义——否则 ILSpy 仍按 event 输出，编译器生成的
    /// add_X/remove_X 又对不上基类（CS0115）。
    /// </summary>
    private static void AlignOrphanEventAccessors(TypeDef[] types, HashSet<TypeDef> own, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods)
    {
        var renamed = new HashSet<MethodDef>();
        foreach (var type in types)
        {
            if (type.IsInterface) continue;
            var chain = BaseChain(type, own).ToArray();
            if (chain.Length == 0) continue;
            var accessors = type.Methods
                .Where(m => !m.IsStatic && m.IsVirtual && !m.IsNewSlot && !m.Name.String.Contains('.')
                    && AccessorPrefix(m.Name.String).Length > 0)
                .ToArray();
            if (accessors.Length == 0) continue;
            foreach (var group in accessors.GroupBy(m => m.MethodSig?.ToString() ?? string.Empty).ToArray())
            {
                var local = group.OrderBy(m => m.MDToken.Raw).ToArray();
                // 已经能按名字对上基类成员的，本来就是正确的重写，不动。
                if (local.Any(m => chain.Any(entry => entry.Definition.Methods.Any(baseMethod => baseMethod.IsVirtual
                        && baseMethod.Name.String == m.Name.String
                        && Signature.Equals(Close(baseMethod.MethodSig, entry.Arguments), m.MethodSig))))) continue;
                // 只认最近的、真正带着这一组抽象成员的基类，跨层混排会把顺序搞乱。
                var nearest = chain
                    .Select(entry => new
                    {
                        Entry = entry,
                        Methods = entry.Definition.Methods
                            .Where(baseMethod => baseMethod.IsVirtual && baseMethod.IsAbstract && !baseMethod.IsStatic
                                && (baseMethod.Attributes & MethodAttributes.SpecialName) != 0
                                && AccessorPrefix(baseMethod.Name.String).Length == 0
                                && Signature.Equals(Close(baseMethod.MethodSig, entry.Arguments), local[0].MethodSig))
                            .ToArray(),
                    })
                    .FirstOrDefault(item => item.Methods.Length > 0);
                if (nearest == null || nearest.Methods.Length != local.Length) continue;
                var targets = nearest.Methods.OrderBy(m => m.MDToken.Raw).ToArray();
                if (targets.Select(m => m.Name.String).Distinct().Count() != targets.Length) continue;
                for (int index = 0; index < local.Length; index++)
                {
                    ForceName(local[index], targets[index].Name.String, changes, originalMethods);
                    local[index].Attributes &= ~MethodAttributes.SpecialName;
                    Align(local[index], targets[index]);
                    renamed.Add(local[index]);
                }
            }
            // 访问器已经不叫 add_X/remove_X 了，事件定义留着只会让 ILSpy 继续
            // 输出 event 语法，而 C# 生成的访问器名字又对不上基类。
            foreach (var definition in type.Events.ToArray())
            {
                bool touched = (definition.AddMethod != null && renamed.Contains(definition.AddMethod))
                    || (definition.RemoveMethod != null && renamed.Contains(definition.RemoveMethod));
                if (touched) type.Events.Remove(definition);
            }
        }
    }

    /// <summary>
    /// 事件定义里的访问器名字必须正好是 add_X / remove_X。名字被抹掉之后
    /// （add_Event0003 被改成基类那对抽象成员的名字），事件定义就名不副实了：
    /// ILSpy 仍按 event 语法输出，而 C# 编译器生成的访问器名字又对不上基类/
    /// 接口成员（CS0115 / CS0535）。这种定义直接拆掉，让访问器以普通方法出现。
    /// </summary>
    private static void StripMismatchedEventDefinitions(TypeDef[] types)
    {
        foreach (var type in types)
        {
            foreach (var definition in type.Events.ToArray())
            {
                string name = definition.Name.String;
                bool addOk = definition.AddMethod == null
                    || definition.AddMethod.Name.String == "add_" + name;
                bool removeOk = definition.RemoveMethod == null
                    || definition.RemoveMethod.Name.String == "remove_" + name;
                if (!addOk || !removeOk) type.Events.Remove(definition);
            }
        }
    }

    /// <summary>访问器前缀（get_/set_/add_/remove_），普通成员返回空串。</summary>
    private static string AccessorPrefix(string name)
    {        int dot = name.LastIndexOf('.');
        var plain = name.Substring(dot + 1);
        if (plain.StartsWith("get_", StringComparison.Ordinal)) return "get_";
        if (plain.StartsWith("set_", StringComparison.Ordinal)) return "set_";
        if (plain.StartsWith("add_", StringComparison.Ordinal)) return "add_";
        if (plain.StartsWith("remove_", StringComparison.Ordinal)) return "remove_";
        return string.Empty;
    }

    /// <summary>
    /// 属性名必须和访问器名一致（get_X/set_X → X），否则反编译出来的属性名和基类
    /// 对不上（CS0115），或者属性里挂着一个名字不相干的访问器（CS0082）。
    /// 拆成"只有 getter"和"只有 setter"两个 PropertyDef 的碎片再合并回一个。
    /// </summary>
    private static void AlignPropertyNames(TypeDef[] types, HashSet<TypeDef> own, List<NameChange> changes)
    {
        foreach (var type in types)
        {
            // 属性名必须来自访问器名（get_X → X）。名字对不上说明属性定义和基类
            // 那边对不上：基类同名同签名的成员只是个普通方法，ILSpy 却会在派生类
            // 这边输出"override 属性"，编译直接 CS0115。但也不能直接拆掉属性——
            // 外部接口（IReadOnlyList 之类）还要靠它实现索引器。正确做法是保留
            // 原名继续重写，另外造一个 get_X 转发方法挂到属性上。
            if (!type.IsInterface)
            {
                var chain = BaseChain(type, own).ToArray();
                foreach (var property in type.Properties.ToArray())
                {
                    var accessor = property.GetMethod ?? property.SetMethod;
                    if (accessor == null || accessor.IsNewSlot || !accessor.IsVirtual) continue;
                    if (accessor.Name.String.Contains('.')) continue;
                    if (AccessorPrefix(accessor.Name.String).Length > 0) continue;
                    bool overridesMethod = chain.Any(entry => entry.Definition.Methods.Any(m => m.IsVirtual
                        && !m.IsStatic && m.Name.String == accessor.Name.String
                        && Signature.Equals(Close(m.MethodSig, entry.Arguments), accessor.MethodSig)));
                    if (!overridesMethod) continue;
                    bool getter = property.GetMethod == accessor;
                    string wanted = (getter ? "get_" : "set_") + property.Name.String;
                    var forwarder = CloneDelegating(type, accessor, wanted, true, new HashSet<MethodDef>(), changes);
                    if (forwarder == accessor) continue;
                    property.GetMethods.Remove(accessor);
                    property.SetMethods.Remove(accessor);
                    if (getter) property.GetMethods.Add(forwarder);
                    else property.SetMethods.Add(forwarder);
                }
            }
            foreach (var property in type.Properties.ToArray())
            {
                var accessor = property.GetMethod ?? property.SetMethod;
                if (accessor == null) continue;
                string name = accessor.Name.String;
                int dot = name.LastIndexOf('.');
                string prefix = dot >= 0 ? name.Substring(0, dot + 1) : string.Empty;
                string plain = name.Substring(dot + 1);
                if (!plain.StartsWith("get_", StringComparison.Ordinal)
                    && !plain.StartsWith("set_", StringComparison.Ordinal)) continue;
                string wanted = prefix + plain.Substring(4);
                if (property.Name.String == wanted) continue;
                var existing = type.Properties.FirstOrDefault(p => p != property && p.Name.String == wanted
                    && SameShape(p.PropertySig, property.PropertySig));
                if (existing != null)
                {
                    // 目标属性已经存在：能合并就合并（访问器互不重叠），否则不动。
                    if (!MergeProperties(type, existing, property)) continue;
                    continue;
                }
                property.Name = wanted;
            }
            // 同一个属性被拆成两个 PropertyDef（一个只有 getter、一个只有 setter）：
            // 名字和形状都一样时合成一个，否则 C# 只认其中一个（CS0082/CS0115）。
            // 只有访问器互不重叠（各自只带 getter 或只带 setter）时才合并。
            foreach (var group in type.Properties.GroupBy(p => p.Name.String + "|" + p.PropertySig)
                         .Where(g => g.Count() > 1).ToArray())
            {
                var parts = group.ToArray();
                if (parts.Count(p => p.GetMethods.Count > 0) > 1) continue;
                if (parts.Count(p => p.SetMethods.Count > 0) > 1) continue;
                var target = parts[0];
                foreach (var part in parts.Skip(1)) MergeProperties(type, target, part);
            }
        }
    }

    /// <summary>把 source 的访问器并进 target 并删除 source；访问器重叠就不合并。</summary>
    private static bool MergeProperties(TypeDef type, PropertyDef target, PropertyDef source)
    {
        if (target == source) return false;
        if (source.GetMethods.Count > 0 && target.GetMethods.Count > 0) return false;
        if (source.SetMethods.Count > 0 && target.SetMethods.Count > 0) return false;
        foreach (var getter in source.GetMethods.ToArray())
            if (!target.GetMethods.Contains(getter)) target.GetMethods.Add(getter);
        foreach (var setter in source.SetMethods.ToArray())
            if (!target.SetMethods.Contains(setter)) target.SetMethods.Add(setter);
        type.Properties.Remove(source);
        return true;
    }

    // ---------------------------------------------------------------- 接口契约

    private static void SyncContracts(TypeDef[] types, HashSet<TypeDef> own, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods, Dictionary<PropertyDef, string> originalProperties)
    {
        // 先统一接口继承链上重声明的名字，后面按名字匹配才不会漏（CS0535）。
        foreach (var contract in types.Where(t => t.IsInterface).ToArray())
            SyncInterfaceRedeclarations(contract, types, own, changes, originalMethods);
        // 接口里的孤儿访问器必须先补成属性：否则契约属性只有半个形状，
        // 实现类那边多出来的 setter 永远配不上（CS0535）。
        foreach (var contract in types.Where(t => t.IsInterface).ToArray())
            RecoverOrphanAccessors(contract);
        foreach (var type in types)
        {
            if (type.IsInterface) continue;
            var explicitLinks = new Dictionary<MethodDef, MethodDef>();
            foreach (var method in type.Methods)
                foreach (var ov in method.Overrides)
                    if (ov.MethodDeclaration.ResolveMethodDef() is { } declaration)
                        explicitLinks[method] = declaration;
            var contracts = Interfaces(type, own).ToArray();
            // 一个实现方法只能服务一个契约成员：认领记录要跨契约属性保持，
            // 否则签名兜底会把已经名花有主的实现再抓一次（抓出 3 个候选，
            // 一个都认不出）。
            var claimed = new HashSet<MethodDef>();
            // 一个方法体只能挂一个名字。被契约点过名的方法记在这里，后面别的契约
            // 再要一个不同的名字时就不再改名，而是造一个转发方法（见 Bind）。
            var committed = new HashSet<MethodDef>();
            var chain = BaseChain(type, own).ToArray();
            // 第一轮：.override 行是权威的显式实现关系，先按"声明接口.成员"命名，
            // 否则后面按名字/签名猜的时候会把它抢走（CS0539/CS0106）。
            ApplyExplicitLinks(type, own, explicitLinks, changes, originalMethods);
            // 第二轮：没有 .override 的成员按名字/签名与契约对齐。
            foreach (var contract in contracts)
                SyncInterfaceContract(type, contract, explicitLinks, claimed, committed, chain,
                    changes, originalMethods, originalProperties);
            // 兜底：点名的访问器必须挂在属性上，否则 ILSpy 按普通方法输出，
            // C# 报 CS0683（显式方法实现不能实现接口访问器）。
            RecoverOrphanAccessors(type);
        }
        // 接口里的孤儿访问器同样要补成属性：否则 ILSpy 把接口成员输出成普通方法，
        // 实现类的属性访问器就没法实现它（CS0686/CS0737）。
        foreach (var contract in types.Where(t => t.IsInterface).ToArray())
            RecoverOrphanAccessors(contract);
    }

    /// <summary>
    /// 访问器（get_X / set_X，显式实现是 Interface.get_X）如果还孤零零地挂在任何
    /// 属性外面，就按访问器签名把属性补出来——索引器的访问器被混淆器拆散后就是
    /// 这样，ILSpy 会把它当普通方法输出（CS0683 / CS0082 / CS0535）。
    /// </summary>
    private static void RecoverOrphanAccessors(TypeDef type)
    {
        foreach (var method in type.Methods.ToArray())
        {
            var name = method.Name.String;
            int dot = name.LastIndexOf('.');
            var plain = name.Substring(dot + 1);
            bool getter = plain.StartsWith("get_", StringComparison.Ordinal);
            bool setter = plain.StartsWith("set_", StringComparison.Ordinal);
            if (!getter && !setter) continue;
            if (type.Properties.Any(p => p.GetMethods.Contains(method) || p.SetMethods.Contains(method))) continue;
            var signature = method.MethodSig;
            if (signature == null) continue;
            string propertyName = plain.Substring(4);
            // 同名成员已经存在（普通方法/字段）：补属性会撞名，宁可不补。
            if (type.Methods.Any(m => m != method && m.Name.String == propertyName)) continue;
            var shape = AccessorPropertySig(signature, setter);
            var target = type.Properties.FirstOrDefault(p => p.Name.String == propertyName
                && SameShape(p.PropertySig, shape));
            // 同名但形状不同的属性已经存在：再补一个就是重载索引器，ILSpy 只认一个
            // （CS0111/CS0620），不补。
            if (target == null)
            {
                if (type.Properties.Any(p => p.Name.String == propertyName)) continue;
                target = new PropertyDefUser(propertyName, shape);
                type.Properties.Add(target);
            }
            if (getter) { target.GetMethods.Clear(); target.GetMethods.Add(method); }
            else { target.SetMethods.Clear(); target.SetMethods.Add(method); }
        }
    }

    private static PropertySig AccessorPropertySig(MethodSig signature, bool setter)
    {
        var parameters = new List<TypeSig>();
        for (int index = 0; index < signature.Params.Count; index++)
        {
            if (setter && index == signature.Params.Count - 1) continue; // value 参数
            parameters.Add(signature.Params[index]);
        }
        // setter 返回 void，属性类型是最后一个（value）参数的类型；拿返回值当
        // 属性类型会造出 void 索引器（CS0620）。
        var type = setter && signature.Params.Count > 0
            ? signature.Params[signature.Params.Count - 1]
            : signature.RetType;
        return new PropertySig(signature.HasThis, type, parameters.ToArray());
    }

    /// <summary>
    /// .override 行直接说明"这个私有方法就是那个接口成员的实现"，按
    /// "声明接口.成员"套名字。声明接口也可以只是基类链上间接继承来的
    /// （C# 允许 <c>void IBase.M()</c> 这样的显式实现）。套上名字还不够：
    /// C# 编译出来的显式实现一定是 private final，少了 final ILSpy 会多写一个
    /// virtual 修饰符，直接 CS0106。
    /// </summary>
    private static void ApplyExplicitLinks(TypeDef type, HashSet<TypeDef> own,
        Dictionary<MethodDef, MethodDef> explicitLinks, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods)
    {
        var implemented = InterfaceClosure(type, own);
        int applied = 0, total = 0;
        foreach (var pair in explicitLinks)
        {
            var body = pair.Key;
            var declaration = pair.Value;
            var owner = declaration.DeclaringType;
            total++;
            if (owner == null || !owner.IsInterface || !own.Contains(owner)) continue;
            if (!implemented.Contains(owner)) continue;
            string wanted = owner.Name.String + "." + declaration.Name.String;
            if (body.Name.String != wanted)
                ForceName(body, wanted, changes, originalMethods);
            if (body.IsPrivate)
                body.Attributes |= MethodAttributes.Final;
            applied++;
        }
    }

    /// <summary>类型实现的所有接口（含基类链上间接继承来的）。</summary>
    private static HashSet<TypeDef> InterfaceClosure(TypeDef type) => InterfaceClosure(type, new HashSet<TypeDef>());

    /// <summary>类型实现的所有接口（含基类链上间接继承来的）。</summary>
    private static HashSet<TypeDef> InterfaceClosure(TypeDef type, HashSet<TypeDef> _)
    {
        var closure = new HashSet<TypeDef>();
        var pending = new Stack<TypeDef>();
        foreach (var item in type.Interfaces)
            if (item.Interface.ResolveTypeDef() is { } contract)
                pending.Push(contract);
        while (pending.Count > 0)
        {
            var contract = pending.Pop();
            if (!closure.Add(contract)) continue;
            foreach (var item in contract.Interfaces)
                if (item.Interface.ResolveTypeDef() is { } inherited)
                    pending.Push(inherited);
        }
        return closure;
    }

    private static IEnumerable<TypeDef> Interfaces(TypeDef type, HashSet<TypeDef> own)
    {
        foreach (var contract in type.Interfaces.Select(i => i.Interface.ResolveTypeDef()))
        {
            if (contract != null && own.Contains(contract) && contract != type && contract.IsInterface)
                yield return contract;
        }
    }

    /// <summary>
    /// 接口继承链上的重声明：派生接口会把基接口的成员再声明一遍（IL 允许同签名
    /// 两个抽象成员），实现类通常只写一个方法，而且按最初声明的名字实现。派生
    /// 接口里那个副本名字不一样，C# 就认为接口成员没实现（CS0535）。这里把副本
    /// 改成最初声明的名字——除非有实现类真的分别实现了这两个成员。
    /// </summary>
    private static void SyncInterfaceRedeclarations(TypeDef contract, TypeDef[] types, HashSet<TypeDef> own,
        List<NameChange> changes, Dictionary<MethodDef, string> originalMethods)
    {
        var bases = InterfaceClosure(contract, own);
        if (bases.Count == 0) return;
        var implementers = types.Where(t => !t.IsInterface && InterfaceClosure(t).Contains(contract)).ToArray();
        foreach (var member in contract.Methods.ToArray())
        {
            if (member.IsConstructor) continue;
            if ((member.SemanticsAttributes & MethodSemanticsAttributes.Getter) != 0
                || (member.SemanticsAttributes & MethodSemanticsAttributes.Setter) != 0) continue;
            var candidates = bases.SelectMany(b => b.Methods)
                .Where(m => Signature.Equals(m.MethodSig, member.MethodSig))
                .Select(m => m.Name.String).Distinct().ToArray();
            if (candidates.Length == 0) continue;
            if (candidates.Contains(member.Name.String)) continue; // 已经和某个基接口成员同名
            // 有实现类分别实现了这两个成员，说明真是两个不同成员，不能合并名字。
            bool distinguished = implementers.Any(t => t.Methods.Any(m => m.Name.String == member.Name.String
                    && Signature.Equals(m.MethodSig, member.MethodSig))
                && t.Methods.Any(m => candidates.Contains(m.Name.String) && Signature.Equals(m.MethodSig, member.MethodSig)));
            if (distinguished) continue;
            string target = candidates[0];
            // 目标名字不能已经被本接口里同签名的成员占用。
            if (contract.Methods.Any(m => m != member && m.Name.String == target
                && Signature.Equals(m.MethodSig, member.MethodSig))) continue;
            ForceName(member, target, changes, originalMethods);
        }
    }

    private static void SyncInterfaceContract(TypeDef type, TypeDef contract,
        Dictionary<MethodDef, MethodDef> explicitLinks, HashSet<MethodDef> claimed, HashSet<MethodDef> committed,
        BaseTypeInfo[] chain, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods, Dictionary<PropertyDef, string> originalProperties)
    {
        string prefix = contract.Name + ".";

        var map = new Dictionary<MethodDef, MethodDef>();
        foreach (var pair in explicitLinks)
            if (pair.Value.DeclaringType == contract)
                map[pair.Value] = pair.Key;
        foreach (var member in contract.Methods.Where(m => !map.ContainsKey(m)).ToArray())
        {
            // 已经被别的接口按 .override 认领过的实现不要抢：否则显式实现的名字
            // 会被后来按签名猜中的契约覆盖，反编译出来 CS0539。
            // 名字相同还不够：索引器/重载成员会同名不同参（get_Item(string) 和
            // get_Item(int)），只按名字取会把两个契约成员指到同一个方法上，另一个
            // 就永远没人实现（CS0535/CS0470）。签名必须一起比。
            var found = type.Methods.FirstOrDefault(m => m.Name.String == member.Name.String
                && !m.Name.String.Contains('.') && Signature.Equals(m.MethodSig, member.MethodSig));
            if (found == null && member.IsAbstract)
            {
                var closed = member.MethodSig;
                var pool = type.Methods.Where(m => m.IsVirtual && !m.Name.String.Contains('.')
                    && !explicitLinks.ContainsKey(m)
                    && Signature.Equals(m.MethodSig, closed)).ToArray();
                // 优先挑还没被别的契约认领的；都被认领过就用现成的，后面 Bind
                // 会为这个契约另造转发方法，总比没人实现强（CS0535）。
                var free = pool.Where(m => !committed.Contains(m)).ToArray();
                var all = free.Length > 0 ? free : pool;
                if (all.Length == 1) found = all[0];
            }
            // 事件的 add/remove 是一对同签名的特殊方法。混淆器把名字抹掉以后，
            // 按名字永远配不上，只能按声明顺序跟契约里同签名的那一组一一对应。
            if (found == null)
            {
                var local = type.Methods.Where(m => m.IsVirtual && !m.IsStatic && !m.Name.String.Contains('.')
                        && !committed.Contains(m) && AccessorPrefix(m.Name.String).Length > 0
                        && Signature.Equals(m.MethodSig, member.MethodSig))
                    .OrderBy(m => m.MDToken.Raw).ToArray();
                var peers = contract.Methods.Where(m => !m.IsConstructor
                        && Signature.Equals(m.MethodSig, member.MethodSig))
                    .OrderBy(m => m.MDToken.Raw).ToArray();
                if (local.Length > 1 && peers.Length == local.Length)
                {
                    int at = System.Array.IndexOf(peers, member);
                    if (at >= 0) found = local[at];
                }
            }
            if (found != null) map[member] = found;
        }

        foreach (var pair in map)
        {
            var member = pair.Key;
            var body = pair.Value;
            if (body == member) continue;
            if ((member.SemanticsAttributes & MethodSemanticsAttributes.Getter) != 0
                || (member.SemanticsAttributes & MethodSemanticsAttributes.Setter) != 0) continue; // 访问器在 MirrorProperty 里处理
            // 私有虚方法是显式实现的形状（C# 的显式实现一定是 private）。
            // 按隐式实现补名会把它变成公开成员，可见性就变了；保持私有加点号。
            bool explicitShape = explicitLinks.ContainsKey(body) || (body.IsPrivate && body.IsVirtual);
            if (explicitShape)
            {
                ForceName(body, prefix + member.Name.String, changes, originalMethods);
                // private final 才是 C# 显式实现的形状，否则 ILSpy 多写 virtual（CS0106）。
                if (body.IsPrivate) body.Attributes |= MethodAttributes.Final;
            }
            else
            {
                body = Bind(type, chain, body, member.Name.String, false, committed, changes, originalMethods);
                body.Attributes = body.Attributes & ~MethodAttributes.MemberAccessMask | MethodAttributes.Public;
            }
        }

        foreach (var contractProperty in contract.Properties)
            MirrorProperty(type, contractProperty, map, prefix, explicitLinks, claimed, committed, chain,
                changes, originalMethods, originalProperties);
    }

    /// <summary>
    /// 让实现类型的属性形状与契约属性一致：契约有几个属性、各带哪些访问器，
    /// 实现类型就要有同名的属性、挂着同样的访问器。混淆器会把一个属性拆成
    /// getter/setter 两个 PropertyDef，或者把访问器挂到别的属性名下。
    /// </summary>
    private static void MirrorProperty(TypeDef type, PropertyDef contractProperty,
        Dictionary<MethodDef, MethodDef> map, string prefix, Dictionary<MethodDef, MethodDef> explicitLinks,
        HashSet<MethodDef> claimed, HashSet<MethodDef> committed, BaseTypeInfo[] chain,
        List<NameChange> changes, Dictionary<MethodDef, string> originalMethods,
        Dictionary<PropertyDef, string> originalProperties)
    {
        var matched = new List<(MethodDef Body, bool Getter)>();
        foreach (var accessor in new[] { contractProperty.GetMethod, contractProperty.SetMethod })
        {
            if (accessor == null) continue;
            bool getter = accessor == contractProperty.GetMethod;
            if (map.TryGetValue(accessor, out var linked) && !claimed.Contains(linked))
            {
                matched.Add((linked, getter)); claimed.Add(linked); continue;
            }
            // 同名还不够：访问器会重载（get_Item(string) / get_Item(int)），
            // 签名必须一致才认，否则会把 string 索引器的访问器挂到 int 上。
            var found = type.Methods.FirstOrDefault(m => !claimed.Contains(m)
                && m.Name.String == accessor.Name.String && Signature.Equals(m.MethodSig, accessor.MethodSig));
            if (found == null && accessor.IsAbstract)
            {
                // 签名兜底：实现里名字对不上（混淆后访问器被摘到别的属性名下），
                // 找唯一一个签名一致的。已经点名的显式实现不抢。
                var bySignature = type.Methods.Where(m => !claimed.Contains(m)
                    && !m.Name.String.Contains('.')
                    && !explicitLinks.ContainsKey(m) && Signature.Equals(m.MethodSig, accessor.MethodSig)).ToArray();
                var free = bySignature.Where(m => !committed.Contains(m)).ToArray();
                if (free.Length > 0) bySignature = free;
                if (bySignature.Length == 1) found = bySignature[0];
            }
            if (found != null) { matched.Add((found, getter)); claimed.Add(found); }
        }
        if (matched.Count == 0) return;


        for (int index = 0; index < matched.Count; index++)
        {
            var (body, getter) = matched[index];
            if (body.Name.String.Contains('.')) continue; // 已经是显式实现的形状
            bool explicitShape = explicitLinks.ContainsKey(body) || (body.IsPrivate && body.IsVirtual);
            string accessorName = (getter ? "get_" : "set_") + contractProperty.Name.String;
            body = Bind(type, chain, body, explicitShape ? prefix + accessorName : accessorName, !explicitShape,
                committed, changes, originalMethods);
            matched[index] = (body, getter);
            if (explicitShape)
            {
                if (body.IsPrivate) body.Attributes |= MethodAttributes.Final;
            }
            else
            {
                body.Attributes = body.Attributes & ~MethodAttributes.MemberAccessMask | MethodAttributes.Public;
            }
        }

        // 把匹配到的访问器从其它属性上摘下来，空壳属性删掉，再统一分配。
        var bodies = matched.Select(m => m.Body).ToArray();
        foreach (var property in type.Properties.ToArray())
        {
            foreach (var body in bodies)
            {
                property.GetMethods.Remove(body);
                property.SetMethods.Remove(body);
            }
            if (property.GetMethods.Count == 0 && property.SetMethods.Count == 0)
                type.Properties.Remove(property);
        }

        // 索引器按参数重载：同名还不行，签名也得对上（Item(string) / Item(int)）。
        var target = type.Properties.FirstOrDefault(p => p.Name.String == contractProperty.Name.String
            && SameShape(p.PropertySig, contractProperty.PropertySig));
        if (target == null)
        {
            var parameters = contractProperty.PropertySig?.Params.Count ?? 0;
            bool overloadable = parameters > 0; // 只有索引器能按参数重载
            if (!overloadable && type.Properties.Any(p => p.Name.String == contractProperty.Name.String))
                return; // 同名但签名不同的普通属性：强行新建会重复定义
            var signature = contractProperty.PropertySig;
            target = new PropertyDefUser(contractProperty.Name.String,
                new PropertySig(signature.HasThis, signature.RetType, signature.Params.ToArray()));
            type.Properties.Add(target);
        }
        foreach (var (body, getter) in matched)
        {
            if (getter) { target.GetMethods.Clear(); target.GetMethods.Add(body); }
            else { target.SetMethods.Clear(); target.SetMethods.Add(body); }
        }
    }

    private static bool SameShape(PropertySig? left, PropertySig? right)
    {
        if (left == null || right == null) return left == null && right == null;
        if (left.HasThis != right.HasThis) return false;
        if (left.Params.Count != right.Params.Count) return false;
        for (int index = 0; index < left.Params.Count; index++)
            if (!Signature.Equals(left.Params[index], right.Params[index])) return false;
        return Signature.Equals(left.RetType, right.RetType);
    }

    // ------------------------------------------------------------ 基类契约与重写

    /// <summary>
    /// ILSpy 的"重写"有两种来源：MethodImpl（.override）和同名同签名的推断。
    /// 混淆器会把基类方法上的 virtual 标志抹掉、把实现方法改名，于是
    /// CS0506 / CS0534 / CS0239。这里把基类契约补齐，并把同名同签名推断出的
    /// 重写关系也修好。
    /// </summary>
    private static void RepairOverrides(TypeDef[] types, HashSet<TypeDef> own, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods)
    {
        foreach (var type in types)
        {
            foreach (var method in type.Methods.ToArray())
            {
                // 显式 .override 行：契约方法必须可重写，可访问性与之对齐。
                foreach (var ov in method.Overrides)
                {
                    var declaration = ov.MethodDeclaration.ResolveMethodDef();
                    if (declaration?.DeclaringType == null || !own.Contains(declaration.DeclaringType)) continue;
                    // "接口.成员"形状的是显式实现：私有、派生类看不见也重写不了，
                    // 当重写目标会抹掉它的 final，反编译 CS0106。混淆器还会把普通
                    // 基类成员的访问性抹成 private，那种仍然要补 virtual/final。
                    if (declaration.Name.String.Contains('.')) continue;
                    declaration.Attributes |= MethodAttributes.Virtual;
                    declaration.Attributes &= ~MethodAttributes.Final;
                    if (!declaration.DeclaringType.IsInterface)
                        Align(method, declaration);
                }

                if (!method.IsVirtual || method.IsConstructor) continue;
                // 带 .override 行、且名字是"接口.成员"形状的方法是显式实现：基类
                // 会各自声明同名的显式实现（同名同签名），不能当成"隐式重写"去把
                // 基类那个的 final 抹掉（CS0106）。普通 MethodImpl 重写不受影响。
                if (method.Overrides.Count > 0 && method.Name.String.Contains('.')) continue;
                // sealed 只有放在 override 上才合法。NewSlot 的方法没有可密封的
                // 重写，带着 final 反编译出来就是 CS0238。但 struct / 静态类里
                // virtual 本身非法（CS0106）：那种类型要把 virtual 和 final 一起
                // 抹掉，反编译成普通方法。
                if (method.IsFinal && method.IsNewSlot && method.Overrides.Count == 0)
                {
                    if (type.IsValueType || (type.IsAbstract && type.IsSealed))
                        method.Attributes &= ~(MethodAttributes.Virtual | MethodAttributes.Final);
                    else if (!type.IsSealed)
                        // 密封类里 "virtual final + 新槽位" 正是 C# 给接口实现生成的形状，
                        // ILSpy 会正常输出成普通方法；清掉 final 反而变成 "new virtual"
                        // 这种密封类型里的非法成员（CS0549）。
                        method.Attributes &= ~MethodAttributes.Final;
                }
                // 隐式重写：沿基类链找同名同签名（需要按泛型实参替换）的成员。
                foreach (var baseType in BaseChain(type, own))
                {
                    var candidate = baseType.Definition.Methods.FirstOrDefault(m => m.Name.String == method.Name.String
                        && Signature.Equals(Close(m.MethodSig, baseType.Arguments), method.MethodSig));
                    if (candidate == null) continue;
                    candidate.Attributes |= MethodAttributes.Virtual;
                    candidate.Attributes &= ~MethodAttributes.Final;
                    // 实现抽象成员是 override，必须是 ReuseSlot：带 NewSlot 会被
                    // C# 当成"隐藏"而不是"实现"（CS0533）。struct 不能 override，
                    // 清了 ReuseSlot 只会让反编译打出非法的 virtual（CS0106）。
                    if (candidate.IsAbstract && !type.IsValueType) method.IsNewSlot = false;
                    Align(method, candidate);
                    break;
                }
            }
        }
    }

    /// <summary>重写方法必须与被重写成员的可访问性完全一致（CS0507）。</summary>
    private static void Align(MethodDef body, MethodDef contract)
    {
        var contractAccess = contract.Attributes & MethodAttributes.MemberAccessMask;
        if ((body.Attributes & MethodAttributes.MemberAccessMask) != contractAccess)
            body.Attributes = body.Attributes & ~MethodAttributes.MemberAccessMask | contractAccess;
    }

    private sealed class BaseTypeInfo
    {
        public TypeDef Definition = null!;
        public IList<TypeSig> Arguments = System.Array.Empty<TypeSig>();
    }

    private static IEnumerable<BaseTypeInfo> BaseChain(TypeDef type, HashSet<TypeDef> own)
    {
        for (var reference = type.BaseType; reference != null;)
        {
            var definition = reference.ResolveTypeDef();
            if (definition == null || !own.Contains(definition)) break;
            var arguments = Arguments(reference);
            yield return new BaseTypeInfo { Definition = definition, Arguments = arguments };
            reference = definition.BaseType == null ? null : Substitute(definition.BaseType.ToTypeSig(), arguments).ToTypeDefOrRef();
        }
    }

    private static IList<TypeSig> Arguments(ITypeDefOrRef reference) =>
        reference is TypeSpec { TypeSig: GenericInstSig inst } ? inst.GenericArguments : System.Array.Empty<TypeSig>();

    // ------------------------------------------------------------ 可访问性归一化

    /// <summary>被引用类型的可访问性不能低于引用它的成员（CS0050/51/52/53/58/60/61）。</summary>
    private static void NormalizeAccessibility(ModuleDef module, TypeDef[] types)
    {
        var definitions = new HashSet<TypeDef>(module.GetTypes());
        bool changed;
        // 可访问性是传递的：别的类型的 public 成员引用本类型时，先把本类型提成
        // public，本类型自己的成员才能理直气壮地要求返回类型也是 public。一顺序
        // 遍历不够（被引用方可能在引用方之后才处理），跑到不动点为止。
        int rounds = 0;
        do
        {
            changed = NormalizeAccessibilityRound(types, definitions);
        }
        while (changed && ++rounds < 16);
    }

    private static bool NormalizeAccessibilityRound(TypeDef[] types, HashSet<TypeDef> definitions)
    {
        var changed = false;
        void Require(TypeDef owner, TypeSig? signature, int required)
        {
            foreach (var referenced in TypeReferences(signature))
                if (referenced != null && definitions.Contains(referenced))
                    changed |= Raise(referenced, required);
        }

        foreach (var type in types)
        {
            // 成员的有效可访问性是"容器可见性"和"自身可见性"里更小的那个：
            // internal 类里的 public 方法其实只有 internal 级，不能反过来要求
            // 返回类型变成 public。
            int typeAccessibility = TypeAccessibility(type);
            if (type.BaseType != null) Require(type, type.BaseType.ToTypeSig(), typeAccessibility);
            foreach (var item in type.Interfaces) Require(type, item.Interface.ToTypeSig(), typeAccessibility);
            foreach (var field in type.Fields)
                Require(type, field.FieldSig?.Type, Math.Min(typeAccessibility, MemberAccessibility(field)));
            foreach (var method in type.Methods)
            {
                int access = Math.Min(typeAccessibility, MemberAccessibility(method));
                Require(type, method.MethodSig?.RetType, access);
                foreach (var parameter in method.MethodSig?.Params ?? System.Array.Empty<TypeSig>())
                    Require(type, parameter, access);
            }
            foreach (var property in type.Properties)
            {
                int access = Math.Min(typeAccessibility, MemberAccessibility(property));
                Require(type, property.PropertySig?.RetType, access);
                foreach (var parameter in property.PropertySig?.Params ?? System.Array.Empty<TypeSig>())
                    Require(type, parameter, access);
            }
            foreach (var item in type.Events)
                Require(type, item.EventType.ToTypeSig(), Math.Min(typeAccessibility, MemberAccessibility(item)));
        }
        return changed;
    }

    private static int TypeAccessibility(TypeDef type) => MemberAccessibility(type);

    private static int MemberAccessibility(TypeDef type)
    {
        int access = type.IsNested
            ? (type.IsNestedPublic ? Visibility.Public : type.IsNestedPrivate ? Visibility.Private
                : type.IsNestedFamily ? Visibility.Protected : type.IsNestedFamilyOrAssembly ? Visibility.ProtectedInternal
                : Visibility.Assembly)
            : (type.IsPublic ? Visibility.Public : Visibility.Assembly);
        var parent = type.DeclaringType;
        while (parent != null)
        {
            access = Math.Min(access, MemberAccessibility(parent));
            parent = parent.DeclaringType;
        }
        return access;
    }

    private static int MemberAccessibility(FieldDef field) => MethodAccessibility(field.Attributes);
    private static int MemberAccessibility(MethodDef method) => MethodAccessibility(method.Attributes);
    private static int MemberAccessibility(EventDef item) => MethodAccessibility(item.AddMethod?.Attributes ?? 0);
    private static int MemberAccessibility(PropertyDef property) =>
        MethodAccessibility((property.GetMethod ?? property.SetMethod)?.Attributes ?? 0);

    private static int MethodAccessibility(MethodAttributes attributes) => (attributes & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Private => Visibility.Private,
        MethodAttributes.Family or MethodAttributes.FamORAssem => Visibility.Protected,
        MethodAttributes.Assembly => Visibility.Assembly,
        MethodAttributes.FamANDAssem => Visibility.ProtectedInternal,
        _ => Visibility.Public,
    };

    /// <summary>字段可访问性（也有 MemberAccessMask）。</summary>
    private static int MethodAccessibility(FieldAttributes attributes) => (attributes & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Private => Visibility.Private,
        FieldAttributes.Family or FieldAttributes.FamORAssem => Visibility.Protected,
        FieldAttributes.Assembly => Visibility.Assembly,
        FieldAttributes.FamANDAssem => Visibility.ProtectedInternal,
        _ => Visibility.Public,
    };

    private static IEnumerable<TypeDef?> TypeReferences(TypeSig? signature)
    {
        switch (signature)
        {
            case null: yield break;
            case TypeDefOrRefSig reference:
                yield return reference.TypeDef;
                yield break;
            case GenericInstSig generic:
                foreach (var argument in generic.GenericArguments)
                foreach (var nested in TypeReferences(argument)) yield return nested;
                yield break;
            case SZArraySig array:
                foreach (var nested in TypeReferences(array.Next)) yield return nested;
                yield break;
            case ArraySig array:
                foreach (var nested in TypeReferences(array.Next)) yield return nested;
                yield break;
            case ByRefSig byReference:
                foreach (var nested in TypeReferences(byReference.Next)) yield return nested;
                yield break;
            case PtrSig pointer:
                foreach (var nested in TypeReferences(pointer.Next)) yield return nested;
                yield break;
            case PinnedSig pinned:
                foreach (var nested in TypeReferences(pinned.Next)) yield return nested;
                yield break;
        }
    }

    /// <summary>把类型连同它的外层类型提升到至少 required 的可见性。返回是否有改动。</summary>
    private static bool Raise(TypeDef type, int required)
    {
        bool changed = false;
        if (MemberAccessibility(type) >= required) return false;
        while (type != null)
        {
            TypeAttributes replacement;
            if (type.IsNested)
            {
                replacement = required >= Visibility.Public ? TypeAttributes.NestedPublic
                    : required >= Visibility.Protected ? TypeAttributes.NestedFamily
                    : TypeAttributes.NestedAssembly;
            }
            else
            {
                replacement = required >= Visibility.Public ? TypeAttributes.Public : TypeAttributes.NotPublic;
            }
            if ((type.Attributes & TypeAttributes.VisibilityMask) != replacement)
            {
                type.Attributes = type.Attributes & ~TypeAttributes.VisibilityMask | replacement;
                changed = true;
            }
            type = type.DeclaringType;
        }
        return changed;
    }

    // ------------------------------------------------------------ 编译器保留特性

    /// <summary>
    /// IsReadOnlyAttribute / TupleElementNamesAttribute 是编译器私有的：原样写进
    /// 源码会掉 CS8335 / CS8141。readonly 修饰符推断已关闭，元组元素名以接口
    /// 契约为准，两边一起抹平后自然一致。
    /// </summary>
    private static void StripCompilerReservedAttributes(ModuleDef module, TypeDef[] types)
    {
        foreach (var type in types)
        {
            Remove(type, "System.Runtime.CompilerServices.IsReadOnlyAttribute");
            Remove(type, "System.Runtime.CompilerServices.TupleElementNamesAttribute");
            foreach (var method in type.Methods)
            {
                Remove(method, "System.Runtime.CompilerServices.TupleElementNamesAttribute");
                foreach (var parameter in method.ParamDefs)
                {
                    Remove(parameter, "System.Runtime.CompilerServices.IsReadOnlyAttribute");
                    Remove(parameter, "System.Runtime.CompilerServices.TupleElementNamesAttribute");
                }
            }
            foreach (var property in type.Properties)
                Remove(property, "System.Runtime.CompilerServices.TupleElementNamesAttribute");
            foreach (var item in type.Events)
            {
                Remove(item, "System.Runtime.CompilerServices.IsReadOnlyAttribute");
                Remove(item, "System.Runtime.CompilerServices.TupleElementNamesAttribute");
            }
            foreach (var item in type.Interfaces)
                Remove(item, "System.Runtime.CompilerServices.TupleElementNamesAttribute");
        }
    }

    private static void Remove(IHasCustomAttribute owner, string fullName)
    {
        for (int index = owner.CustomAttributes.Count - 1; index >= 0; index--)
            if (owner.CustomAttributes[index].TypeFullName == fullName)
                owner.CustomAttributes.RemoveAt(index);
    }

    // ------------------------------------------------------------ 泛型签名替换

    /// <summary>把 !0/!1 这类泛型变量按实际类型实参替换掉，才能和派生侧的签名比较。</summary>
    private static MethodSig Close(MethodSig signature, IList<TypeSig> arguments)
    {
        var closed = signature.Clone();
        closed.RetType = Substitute(closed.RetType, arguments);
        for (int i = 0; i < closed.Params.Count; i++)
            closed.Params[i] = Substitute(closed.Params[i], arguments);
        return closed;
    }

    private static TypeSig Substitute(TypeSig type, IList<TypeSig> arguments) => type switch
    {
        GenericVar variable when variable.Number < arguments.Count => arguments[(int)variable.Number],
        SZArraySig array => new SZArraySig(Substitute(array.Next, arguments)),
        ByRefSig byReference => new ByRefSig(Substitute(byReference.Next, arguments)),
        PtrSig pointer => new PtrSig(Substitute(pointer.Next, arguments)),
        PinnedSig pinned => new PinnedSig(Substitute(pinned.Next, arguments)),
        CModReqdSig modified => new CModReqdSig(modified.Modifier, Substitute(modified.Next, arguments)),
        CModOptSig modified => new CModOptSig(modified.Modifier, Substitute(modified.Next, arguments)),
        GenericInstSig generic => new GenericInstSig(generic.GenericType, generic.GenericArguments.Select(a => Substitute(a, arguments)).ToArray()),
        ArraySig array => new ArraySig(Substitute(array.Next, arguments), array.Rank, array.Sizes, array.LowerBounds),
        _ => type,
    };

    // ------------------------------------------------------------ 改名工具

    /// <summary>
    /// 把实现方法挂到契约要求的名字上。一个方法体只能有一个名字，所以两种情况下
    /// 不能直接改名：
    ///   1. 它已经被别的契约成员认领过（两个接口用不同名字声明了同签名成员）；
    ///   2. 它当前的名字正好隐式重写着基类链上的一个虚成员（改名等于放弃重写）。
    /// 这时保留原方法不动，另造一个同名转发方法给后来者——语义完全一致。
    /// </summary>
    private static MethodDef Bind(TypeDef type, BaseTypeInfo[] chain, MethodDef body, string wanted,
        bool accessor, HashSet<MethodDef> committed, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods)
    {
        if (body.Name.String == wanted) { committed.Add(body); return body; }
        bool pinned = committed.Contains(body)
            || (body.IsVirtual && !body.IsNewSlot && chain.Any(entry => entry.Definition.Methods.Any(m => m.IsVirtual
                && m.Name.String == body.Name.String
                && Signature.Equals(Close(m.MethodSig, entry.Arguments), body.MethodSig))));
        if (!pinned)
        {
            ForceName(body, wanted, changes, originalMethods);
            committed.Add(body);
            return body;
        }
        return CloneDelegating(type, body, wanted, accessor, committed, changes);
    }

    /// <summary>
    /// 造一个"调一下原方法"的转发方法。原方法可能已经被另一个契约成员占用，
    /// 名字不能改，只能复制一个入口出来。
    /// </summary>
    private static MethodDef CloneDelegating(TypeDef type, MethodDef source, string name, bool accessor,
        HashSet<MethodDef> committed, List<NameChange> changes)
    {
        // 这个名字已经有一个同签名的成员了，直接用它，再造一个就是重定义（CS0082）。
        var existing = type.Methods.FirstOrDefault(m => m != source && m.Name.String == name
            && Signature.Equals(m.MethodSig, source.MethodSig));
        if (existing != null) { committed.Add(existing); return existing; }
        var attributes = source.Attributes & ~(MethodAttributes.Abstract | MethodAttributes.SpecialName
            | MethodAttributes.RTSpecialName | MethodAttributes.PinvokeImpl);
        if (accessor) attributes |= MethodAttributes.SpecialName;
        // 接口实现必须是 virtual；值类型里加 virtual 会直接 CS0106。
        if (!type.IsValueType)
        {
            attributes |= MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig;
            // 密封类里的"新虚拟成员"是非法的（CS0549）。C# 自己给密封类生成的接口
            // 实现也带 final，加上它 ILSpy 就不会再写 virtual。
            if (type.IsSealed) attributes |= MethodAttributes.Final;
        }
        var clone = new MethodDefUser(name, source.MethodSig, MethodImplAttributes.IL, attributes);
        var body = new CilBody();
        clone.Body = body;
        int offset = 0;
        if (!source.IsStatic)
        {
            body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
            offset = 1;
        }
        for (int index = 0; index < source.MethodSig.Params.Count; index++)
            body.Instructions.Add(OpCodes.Ldarg.ToInstruction(new Parameter((ushort)(index + offset))));
        body.Instructions.Add(source.IsStatic
            ? OpCodes.Call.ToInstruction(source)
            : (source.IsVirtual ? OpCodes.Callvirt : OpCodes.Call).ToInstruction(source));
        body.Instructions.Add(OpCodes.Ret.ToInstruction());
        type.Methods.Add(clone);
        committed.Add(clone);
        Add(changes, "Method", clone, "(generated forwarder)", name,
            "same signature already serves another contract member; generated a delegating forwarder");
        return clone;
    }

    private static void ForceName(MethodDef method, string name, List<NameChange> changes,
        Dictionary<MethodDef, string> originalMethods)
    {
        if (method.Name.String == name) return;
        Add(changes, "Method", method, originalMethods.TryGetValue(method, out var original) ? original : method.Name.String,
            name, "interface contract requires this member name for C#");
        method.Name = name;
    }

    private static void Add(List<NameChange> changes, string kind, MethodDef method,
        string original, string renamed, string evidence) =>
        changes.Add(new NameChange
        {
            Kind = kind,
            Token = method.MDToken.ToString(),
            Original = original,
            Renamed = renamed,
            Type = method.MethodSig?.RetType?.FullName ?? string.Empty,
            Evidence = evidence,
        });
}
