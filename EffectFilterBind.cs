using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Stellar.MultiTool;

/// <summary>
/// Open-instance delegate binders for <see cref="EffectFilterPatch"/> (ported from the Experiment effect census, which
/// validated them in game): each interop getter/method is turned ONCE into a Func (one cast + one call per read, no
/// <c>Invoke</c>, no boxing). Any shape surprise → null, and the caller reports it unresolved. Enum/uint values come back
/// as long through <see cref="Unsafe"/> (no box, no Convert). Constraint is <c>struct</c>, not <c>unmanaged</c>: the
/// IL2CPP corlib refs lack IsUnmanagedAttribute (CS0656).
/// </summary>
internal static class EffectFilterBind
{
    private const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

    private static MethodInfo G(string name, params Type[] args) =>
        typeof(EffectFilterBind).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(args);

    private static MethodInfo? Getter(Type t, string prop) => t.GetProperty(prop, Pub)?.GetGetMethod();

    public static Func<object, object?>? RefProp(Type t, string prop)
    {
        try { var g = Getter(t, prop); return g == null || g.ReturnType.IsValueType ? null
                : (Func<object, object?>?)G(nameof(OpenRef), t, g.ReturnType).Invoke(null, new object[] { g }); }
        catch { return null; }
    }

    // long / int / uint / byte-backed enum / int-backed enum property → long.
    public static Func<object, long>? NumProp(Type t, string prop)
    {
        try { var g = Getter(t, prop); return g == null || !g.ReturnType.IsValueType ? null
                : (Func<object, long>?)G(nameof(OpenNum), t, g.ReturnType).Invoke(null, new object[] { g }); }
        catch { return null; }
    }

    public static Func<object, bool>? BoolProp(Type t, string prop)
    {
        try { var g = Getter(t, prop); return g == null || g.ReturnType != typeof(bool) ? null
                : (Func<object, bool>?)G(nameof(OpenBool), t).Invoke(null, new object[] { g }); }
        catch { return null; }
    }

    public static Func<long, long>? StaticNum(MethodInfo m)
    {
        try { return (Func<long, long>?)G(nameof(OpenStaticNum), m.ReturnType).Invoke(null, new object[] { m }); }
        catch { return null; }
    }

    public static Func<object?>? StaticRef(MethodInfo? m)
    {
        try { return m == null || !m.IsStatic || m.ReturnType.IsValueType ? null
                : (Func<object?>?)G(nameof(OpenStaticRef), m.ReturnType).Invoke(null, new object[] { m }); }
        catch { return null; }
    }

    public static Func<object, long, object?>? Ref1Long(MethodInfo m)
    {
        try { return m.ReturnType.IsValueType || m.DeclaringType == null ? null
                : (Func<object, long, object?>?)G(nameof(OpenRef1), m.DeclaringType, m.ReturnType).Invoke(null, new object[] { m }); }
        catch { return null; }
    }

    // GetAttr<long>(EAttrType, bool) with the attr key baked in. <long> is a mapped type — never <object>
    // (Il2CppInterop-TypeInjection-Crash.md: System.Object has no IL2CPP class pointer, it throws every call).
    public static Func<object, long>? AttrLong(MethodInfo m, object attrBox)
    {
        try { return m.DeclaringType == null || m.ReturnType != typeof(long) ? null
                : (Func<object, long>?)G(nameof(OpenAttr), m.DeclaringType, attrBox.GetType()).Invoke(null, new[] { m, attrBox }); }
        catch { return null; }
    }

    private static Func<object, object?> OpenRef<T, TR>(MethodInfo m) where T : class where TR : class
    { var d = (Func<T, TR>)Delegate.CreateDelegate(typeof(Func<T, TR>), m); return o => d((T)o); }

    private static Func<object, long> OpenNum<T, TV>(MethodInfo m) where T : class where TV : struct
    { var d = (Func<T, TV>)Delegate.CreateDelegate(typeof(Func<T, TV>), m); return o => AsLong(d((T)o)); }

    private static Func<object, bool> OpenBool<T>(MethodInfo m) where T : class
    { var d = (Func<T, bool>)Delegate.CreateDelegate(typeof(Func<T, bool>), m); return o => d((T)o); }

    private static Func<long, long> OpenStaticNum<TV>(MethodInfo m) where TV : struct
    { var d = (Func<long, TV>)Delegate.CreateDelegate(typeof(Func<long, TV>), m); return u => AsLong(d(u)); }

    private static Func<object?> OpenStaticRef<TR>(MethodInfo m) where TR : class
    { var d = (Func<TR>)Delegate.CreateDelegate(typeof(Func<TR>), m); return () => d(); }

    private static Func<object, long, object?> OpenRef1<T, TR>(MethodInfo m) where T : class where TR : class
    { var d = (Func<T, long, TR>)Delegate.CreateDelegate(typeof(Func<T, long, TR>), m); return (o, a) => d((T)o, a); }

    private static Func<object, long> OpenAttr<T, TE>(MethodInfo m, object box) where T : class where TE : struct
    {
        var d = (Func<T, TE, bool, long>)Delegate.CreateDelegate(typeof(Func<T, TE, bool, long>), m);
        var attr = (TE)box;
        return o => d((T)o, attr, true);
    }

    // Reinterpret an unmanaged value (enum / integer) as a long by its size — no boxing. 4-byte values are read
    // unsigned (uint Addr; EEntityType / byte enums are small and non-negative).
    private static long AsLong<TV>(TV v) where TV : struct
    {
        switch (Unsafe.SizeOf<TV>())
        {
            case 1:  return Unsafe.As<TV, byte>(ref v);
            case 2:  return Unsafe.As<TV, ushort>(ref v);
            case 4:  return Unsafe.As<TV, uint>(ref v);
            case 8:  return Unsafe.As<TV, long>(ref v);
            default: return 0;
        }
    }
}
