// Kannu for Windows
// Copyright (C) 2026 Kannu Contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program. If
// not, see <https://www.gnu.org/licenses/>.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Kannu.App;

/// <summary>
/// Notification secrets (ntfy topic, Pushover keys, webhook URL) in Windows Credential Manager, as
/// generic credentials named <c>Kannu/…</c>, encrypted for this user by Windows. Never in
/// settings.json and never in a log. The Windows counterpart of macOS <c>SecureSecretsStore</c>.
/// </summary>
internal static unsafe partial class SecretStore
{
    public const string NtfyTopic = "ntfy-topic";
    public const string PushoverUserKey = "pushover-user-key";
    public const string PushoverAppToken = "pushover-app-token";
    public const string WebhookUrl = "webhook-url";

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    private static string Target(string name) => "Kannu/" + name;

    public static string Get(string name)
    {
        if (!CredReadW(Target(name), CRED_TYPE_GENERIC, 0, out var pointer)) return "";
        try
        {
            var credential = (Credential*)pointer;
            if (credential->CredentialBlob == IntPtr.Zero || credential->CredentialBlobSize == 0) return "";
            return Encoding.Unicode.GetString((byte*)credential->CredentialBlob, (int)credential->CredentialBlobSize);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <summary>An empty value deletes the credential.</summary>
    public static bool Set(string name, string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            CredDeleteW(Target(name), CRED_TYPE_GENERIC, 0);
            return true;
        }
        var blob = Encoding.Unicode.GetBytes(value);
        var target = Marshal.StringToHGlobalUni(Target(name));
        var user = Marshal.StringToHGlobalUni(Environment.UserName);
        try
        {
            fixed (byte* data = blob)
            {
                var credential = new Credential
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = (IntPtr)data,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = user,
                };
                return CredWriteW(&credential, 0);
            }
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(Credential* credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, int flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);
}
