using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Sqloryn.Core;

public interface ISecretStore
{
    Task SetAsync(string key, string secret);
    Task<string?> GetAsync(string key);
    Task DeleteAsync(string key);
}

public static class ProfileSecretKeys
{
    public static string ForProfile(Guid id) => "profile:" + id;
}

public sealed class MemorySecretStore : ISecretStore
{
    readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public Task SetAsync(string key, string secret)
    {
        SecretKey.Validate(key);
        ArgumentNullException.ThrowIfNull(secret);
        _secrets[key] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key)
    {
        SecretKey.Validate(key);
        return Task.FromResult(_secrets.TryGetValue(key, out var value) ? value : null);
    }

    public Task DeleteAsync(string key)
    {
        SecretKey.Validate(key);
        _secrets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class MacKeychainSecretStore : ISecretStore
{
    public const string ServiceName = "org.sqloryn.app";

    public static bool IsSupported => OperatingSystem.IsMacOS();

    public Task SetAsync(string key, string secret)
    {
        SecretKey.Validate(key);
        ArgumentNullException.ThrowIfNull(secret);
        EnsureSupported();
        return Task.Run(() => Keychain.Set(key, Encoding.UTF8.GetBytes(secret)));
    }

    public Task<string?> GetAsync(string key)
    {
        SecretKey.Validate(key);
        EnsureSupported();
        return Task.Run(() =>
        {
            var bytes = Keychain.Get(key);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        });
    }

    public Task DeleteAsync(string key)
    {
        SecretKey.Validate(key);
        EnsureSupported();
        return Task.Run(() => Keychain.Delete(key));
    }

    static void EnsureSupported()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Keychain storage is only available on macOS.");
    }
}

static class SecretKey
{
    public static void Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl))
            throw new ArgumentException("Invalid secret key.", nameof(key));
    }
}

static class Keychain
{
    const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const uint Utf8Encoding = 0x08000100;
    const int ErrSecSuccess = 0;
    const int ErrSecItemNotFound = -25300;

    [DllImport(SecurityLib)]
    static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityLib)]
    static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityLib)]
    static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport(SecurityLib)]
    static extern int SecItemDelete(IntPtr query);

    [DllImport(CoreFoundationLib)]
    static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundationLib)]
    static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);

    [DllImport(CoreFoundationLib)]
    static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundationLib)]
    static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationLib)]
    static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationLib)]
    static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationLib)]
    static extern void CFRelease(IntPtr value);

    static readonly Lazy<Symbols> Sym = new(() => new Symbols());

    sealed class Symbols
    {
        public readonly IntPtr KeyCallBacks;
        public readonly IntPtr ValueCallBacks;
        public readonly IntPtr SecClass;
        public readonly IntPtr SecClassGenericPassword;
        public readonly IntPtr SecAttrService;
        public readonly IntPtr SecAttrAccount;
        public readonly IntPtr SecValueData;
        public readonly IntPtr SecReturnData;
        public readonly IntPtr SecMatchLimit;
        public readonly IntPtr SecMatchLimitOne;
        public readonly IntPtr BooleanTrue;

        public Symbols()
        {
            var cf = NativeLibrary.Load(CoreFoundationLib);
            var sec = NativeLibrary.Load(SecurityLib);
            KeyCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
            ValueCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
            BooleanTrue = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
            SecClass = Constant(sec, "kSecClass");
            SecClassGenericPassword = Constant(sec, "kSecClassGenericPassword");
            SecAttrService = Constant(sec, "kSecAttrService");
            SecAttrAccount = Constant(sec, "kSecAttrAccount");
            SecValueData = Constant(sec, "kSecValueData");
            SecReturnData = Constant(sec, "kSecReturnData");
            SecMatchLimit = Constant(sec, "kSecMatchLimit");
            SecMatchLimitOne = Constant(sec, "kSecMatchLimitOne");
        }

        static IntPtr Constant(IntPtr library, string name)
            => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }

    public static void Set(string account, byte[] secret)
    {
        var s = Sym.Value;
        var owned = new List<IntPtr>();
        try
        {
            var query = BaseQuery(s, account, owned);
            var update = NewDictionary(s, owned);
            CFDictionarySetValue(update, s.SecValueData, NewData(secret, owned));
            var status = SecItemUpdate(query, update);
            if (status == ErrSecItemNotFound)
            {
                var add = BaseQuery(s, account, owned);
                CFDictionarySetValue(add, s.SecValueData, NewData(secret, owned));
                status = SecItemAdd(add, IntPtr.Zero);
            }
            ThrowIfFailed(status, "store");
        }
        finally
        {
            ReleaseAll(owned);
        }
    }

    public static byte[]? Get(string account)
    {
        var s = Sym.Value;
        var owned = new List<IntPtr>();
        IntPtr result = IntPtr.Zero;
        try
        {
            var query = BaseQuery(s, account, owned);
            CFDictionarySetValue(query, s.SecReturnData, s.BooleanTrue);
            CFDictionarySetValue(query, s.SecMatchLimit, s.SecMatchLimitOne);
            var status = SecItemCopyMatching(query, out result);
            if (status == ErrSecItemNotFound)
                return null;
            ThrowIfFailed(status, "read");
            if (result == IntPtr.Zero)
                return null;
            var length = (int)CFDataGetLength(result);
            var bytes = new byte[length];
            if (length > 0)
                Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
            return bytes;
        }
        finally
        {
            if (result != IntPtr.Zero)
                CFRelease(result);
            ReleaseAll(owned);
        }
    }

    public static void Delete(string account)
    {
        var s = Sym.Value;
        var owned = new List<IntPtr>();
        try
        {
            var status = SecItemDelete(BaseQuery(s, account, owned));
            if (status != ErrSecItemNotFound)
                ThrowIfFailed(status, "delete");
        }
        finally
        {
            ReleaseAll(owned);
        }
    }

    static IntPtr BaseQuery(Symbols s, string account, List<IntPtr> owned)
    {
        var dict = NewDictionary(s, owned);
        CFDictionarySetValue(dict, s.SecClass, s.SecClassGenericPassword);
        CFDictionarySetValue(dict, s.SecAttrService, NewString(MacKeychainSecretStore.ServiceName, owned));
        CFDictionarySetValue(dict, s.SecAttrAccount, NewString(account, owned));
        return dict;
    }

    static IntPtr NewDictionary(Symbols s, List<IntPtr> owned)
    {
        var dict = CFDictionaryCreateMutable(IntPtr.Zero, 0, s.KeyCallBacks, s.ValueCallBacks);
        if (dict == IntPtr.Zero)
            throw new InvalidOperationException("Keychain request could not be created.");
        owned.Add(dict);
        return dict;
    }

    static IntPtr NewString(string value, List<IntPtr> owned)
    {
        var str = CFStringCreateWithCString(IntPtr.Zero, value, Utf8Encoding);
        if (str == IntPtr.Zero)
            throw new InvalidOperationException("Keychain request could not be created.");
        owned.Add(str);
        return str;
    }

    static IntPtr NewData(byte[] bytes, List<IntPtr> owned)
    {
        var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        if (data == IntPtr.Zero)
            throw new InvalidOperationException("Keychain request could not be created.");
        owned.Add(data);
        return data;
    }

    static void ReleaseAll(List<IntPtr> owned)
    {
        foreach (var handle in owned)
            CFRelease(handle);
        owned.Clear();
    }

    static void ThrowIfFailed(int status, string operation)
    {
        if (status != ErrSecSuccess)
            throw new InvalidOperationException($"Keychain {operation} failed (OSStatus {status}).");
    }
}
