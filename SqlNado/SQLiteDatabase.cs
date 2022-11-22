﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SqlNado.Utilities;

namespace SqlNado
{
    public class SQLiteDatabase : IDisposable
    {
        private string _primaryKeyPersistenceSeparator = "\0";
        private static IntPtr _module;
        private IntPtr _handle;
        private bool _enableStatementsCache = true;
        private volatile bool _querySupportFunctionsAdded = false;
        private readonly ConcurrentDictionary<Type, SQLiteBindType> _bindTypes = new ConcurrentDictionary<Type, SQLiteBindType>();
        private readonly ConcurrentDictionary<string, SQLiteObjectTable> _objectTables = new ConcurrentDictionary<string, SQLiteObjectTable>();
        private readonly ConcurrentDictionary<string, ScalarFunctionSink> _functionSinks = new ConcurrentDictionary<string, ScalarFunctionSink>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CollationSink> _collationSinks = new ConcurrentDictionary<string, CollationSink>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SQLiteTokenizer> _tokenizers = new ConcurrentDictionary<string, SQLiteTokenizer>(StringComparer.OrdinalIgnoreCase);

        // note the pool is case-sensitive. it may not be always optimized, but it's safer
        private readonly ConcurrentDictionary<string, StatementPool> _statementPools = new ConcurrentDictionary<string, StatementPool>(StringComparer.Ordinal);
        private readonly collationNeeded _collationNeeded;

        public event EventHandler<SQLiteCollationNeededEventArgs> CollationNeeded;

        static SQLiteDatabase()
        {
            UseWindowsRuntime = true;
        }

        public SQLiteDatabase(string filePath)
            : this(filePath, SQLiteOpenOptions.SQLITE_OPEN_READWRITE | SQLiteOpenOptions.SQLITE_OPEN_CREATE)
        {
        }

        public SQLiteDatabase(string filePath, SQLiteOpenOptions options)
        {
            if (filePath == null)
                throw new ArgumentNullException(nameof(filePath));

            OpenOptions = options;
#if DEBUG
            ErrorOptions |= SQLiteErrorOptions.AddSqlText;
#endif
            BindOptions = CreateBindOptions();
            if (BindOptions == null)
                throw new InvalidOperationException();

            HookNativeProcs();
            CheckError(_sqlite3_open_v2(filePath, out _handle, options, IntPtr.Zero));
            _collationNeeded = NativeCollationNeeded;
            CheckError(_sqlite3_collation_needed16(_handle, IntPtr.Zero, _collationNeeded));
            FilePath = filePath;
            AddDefaultBindTypes();
        }

        public static string NativeDllPath { get; private set; }
        public static bool IsUsingWindowsRuntime { get; private set; }
        public static bool UseWindowsRuntime { get; set; } = true;

        public static bool CanBeThreadSafe => DefaultThreadingMode != SQLiteThreadingMode.SingleThreaded;
        public static SQLiteThreadingMode DefaultThreadingMode
        {
            get
            {
                HookNativeProcs();
                return (SQLiteThreadingMode)_sqlite3_threadsafe();
            }
        }

        [Browsable(false)]
        public IntPtr Handle => _handle;
        public bool IsDisposed => _handle == IntPtr.Zero;
        public string FilePath { get; }
        public SQLiteOpenOptions OpenOptions { get; }
        public IReadOnlyDictionary<Type, SQLiteBindType> BindTypes => _bindTypes;
        public SQLiteBindOptions BindOptions { get; }
        public bool EnforceForeignKeys { get => ExecuteScalar<bool>("PRAGMA foreign_keys"); set => ExecuteNonQuery("PRAGMA foreign_keys=" + (value ? 1 : 0)); }
        public bool DeferForeignKeys { get => ExecuteScalar<bool>("PRAGMA defer_foreign_keys"); set => ExecuteNonQuery("PRAGMA defer_foreign_keys=" + (value ? 1 : 0)); }
        public bool ForeignKeys { get => ExecuteScalar<bool>("PRAGMA foreign_keys"); set => ExecuteNonQuery("PRAGMA foreign_keys=" + (value ? 1 : 0)); }
        public bool ReadUncommited { get => ExecuteScalar<bool>("PRAGMA read_uncommitted"); set => ExecuteNonQuery("PRAGMA read_uncommitted=" + (value ? 1 : 0)); }
        public bool RecursiveTriggers { get => ExecuteScalar<bool>("PRAGMA recursive_triggers"); set => ExecuteNonQuery("PRAGMA recursive_triggers=" + (value ? 1 : 0)); }
        public bool ReverseUnorderedSelects { get => ExecuteScalar<bool>("PRAGMA reverse_unordered_selects"); set => ExecuteNonQuery("PRAGMA reverse_unordered_selects=" + (value ? 1 : 0)); }
        public bool AutomaticIndex { get => ExecuteScalar<bool>("PRAGMA automatic_index"); set => ExecuteNonQuery("PRAGMA automatic_index=" + (value ? 1 : 0)); }
        public bool CellSizeCheck { get => ExecuteScalar<bool>("PRAGMA cell_size_check"); set => ExecuteNonQuery("PRAGMA cell_size_check=" + (value ? 1 : 0)); }
        public bool CheckpointFullFSync { get => ExecuteScalar<bool>("PRAGMA checkpoint_fullfsync"); set => ExecuteNonQuery("PRAGMA checkpoint_fullfsync=" + (value ? 1 : 0)); }
        public bool FullFSync { get => ExecuteScalar<bool>("PRAGMA fullfsync"); set => ExecuteNonQuery("PRAGMA fullfsync=" + (value ? 1 : 0)); }
        public bool IgnoreCheckConstraints { get => ExecuteScalar<bool>("PRAGMA ignore_check_constraints"); set => ExecuteNonQuery("PRAGMA ignore_check_constraints=" + (value ? 1 : 0)); }
        public bool QueryOnly { get => ExecuteScalar<bool>("PRAGMA query_only"); set => ExecuteNonQuery("PRAGMA query_only=" + (value ? 1 : 0)); }
        public int BusyTimeout { get => ExecuteScalar<int>("PRAGMA busy_timeout"); set => ExecuteNonQuery("PRAGMA busy_timeout=" + value); }
        public int CacheSize { get => ExecuteScalar<int>("PRAGMA cache_size"); set => ExecuteNonQuery("PRAGMA cache_size=" + value); }
        public int PageSize { get => ExecuteScalar<int>("PRAGMA page_size"); set => ExecuteNonQuery("PRAGMA page_size=" + value); }
        public long MemoryMapSize { get => ExecuteScalar<int>("PRAGMA mmap_size"); set => ExecuteNonQuery("PRAGMA mmap_size=" + value); }
        public SQLiteSynchronousMode SynchronousMode { get => ExecuteScalar<SQLiteSynchronousMode>("PRAGMA synchronous"); set => ExecuteNonQuery("PRAGMA synchronous=" + value); }
        public SQLiteJournalMode JournalMode { get => ExecuteScalar<SQLiteJournalMode>("PRAGMA journal_mode"); set => ExecuteNonQuery("PRAGMA journal_mode=" + value); }
        public SQLiteLockingMode LockingMode { get => ExecuteScalar<SQLiteLockingMode>("PRAGMA locking_mode"); set => ExecuteNonQuery("PRAGMA locking_mode=" + value); }
        public SQLiteTempStore TempStore { get => ExecuteScalar<SQLiteTempStore>("PRAGMA temp_store"); set => ExecuteNonQuery("PRAGMA temp_store=" + value); }
        public int DataVersion => ExecuteScalar<int>("PRAGMA data_version");
        public IEnumerable<string> CompileOptions => LoadObjects("PRAGMA compile_options").Select(row => (string)row[0]);
        public IEnumerable<string> Collations => LoadObjects("PRAGMA collation_list").Select(row => (string)row[1]);
        public virtual ISQLiteLogger Logger { get; set; }
        public virtual SQLiteErrorOptions ErrorOptions { get; set; }
        public virtual string DefaultColumnCollation { get; set; }

        public virtual bool EnableStatementsCache
        {
            get => _enableStatementsCache;
            set
            {
                _enableStatementsCache = value;
                if (!_enableStatementsCache)
                {
                    ClearStatementsCache();
                }
            }
        }

        public string PrimaryKeyPersistenceSeparator
        {
            get => _primaryKeyPersistenceSeparator;
            set
            {
                if (value == null)
                    throw new ArgumentNullException(nameof(value));

                _primaryKeyPersistenceSeparator = value;
            }
        }

        public IEnumerable<SQLiteTable> Tables
        {
            get
            {
                var options = CreateLoadOptions();
                if (options == null)
                    throw new InvalidOperationException();

                options.GetInstanceFunc = (t, s, o) => new SQLiteTable(this);
                return Load<SQLiteTable>("WHERE type='table'", options);
            }
        }

        public IEnumerable<SQLiteIndex> Indices
        {
            get
            {
                var options = CreateLoadOptions();
                if (options == null)
                    throw new InvalidOperationException();

                options.GetInstanceFunc = (t, s, o) => new SQLiteIndex(this);
                return Load<SQLiteIndex>("WHERE type='index'", options);
            }
        }

        [Browsable(false)]
        public int TotalChangesCount => _sqlite3_total_changes(CheckDisposed());

        [Browsable(false)]
        public int ChangesCount
        {
            get
            {
                var changes = _sqlite3_changes(CheckDisposed());
#if DEBUG
                Log(TraceLevel.Verbose, "Changes: " + changes);
#endif
                return changes;
            }
        }

        [Browsable(false)]
        public long LastInsertRowId => _sqlite3_last_insert_rowid(CheckDisposed());

        private void NativeCollationNeeded(IntPtr arg, IntPtr handle, SQLiteTextEncoding encoding, string name)
        {
            if (name == null)
                return;

            var e = new SQLiteCollationNeededEventArgs(this, name);

            switch (name)
            {
                case nameof(StringComparer.CurrentCulture):
                    SetCollationFunction(name, StringComparer.CurrentCulture);
                    break;

                case nameof(StringComparer.CurrentCultureIgnoreCase):
                    SetCollationFunction(name, StringComparer.CurrentCultureIgnoreCase);
                    break;

                case nameof(StringComparer.Ordinal):
                    SetCollationFunction(name, StringComparer.Ordinal);
                    break;

                case nameof(StringComparer.OrdinalIgnoreCase):
                    SetCollationFunction(name, StringComparer.OrdinalIgnoreCase);
                    break;

                case nameof(StringComparer.InvariantCulture):
                    SetCollationFunction(name, StringComparer.InvariantCulture);
                    break;

                case nameof(StringComparer.InvariantCultureIgnoreCase):
                    SetCollationFunction(name, StringComparer.InvariantCultureIgnoreCase);
                    break;

                default:
                    if (e.CollationCulture != null)
                    {
                        SetCollationFunction(name, Extensions.GetStringComparer(e.CollationCulture.CompareInfo, e.CollationOptions));
                    }
                    break;
            }

            // still give a chance to caller to override
            OnCollationNeeded(this, e);
        }

        protected virtual void OnCollationNeeded(object sender, SQLiteCollationNeededEventArgs e) => CollationNeeded?.Invoke(sender, e);

        public virtual void SetCollationFunction(string name, IComparer<string> comparer)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            if (comparer == null)
            {
                CheckError(_sqlite3_create_collation16(CheckDisposed(), name, SQLiteTextEncoding.SQLITE_UTF16, IntPtr.Zero, null));
                _collationSinks.TryRemove(name, out _);
                return;
            }

            var sink = new CollationSink();
            sink.Comparer = comparer;
            _collationSinks[name] = sink;

            // note we only support UTF-16 encoding so we have only ptr > str marshaling
            CheckError(_sqlite3_create_collation16(CheckDisposed(), name, SQLiteTextEncoding.SQLITE_UTF16, IntPtr.Zero, sink.Callback));
        }

        private sealed class CollationSink
        {
            public IComparer<string> Comparer;
            public xCompare Callback;

            public CollationSink()
            {
                Callback = new xCompare(Compare);
            }

            public int Compare(IntPtr arg, int lenA, IntPtr strA, int lenB, IntPtr strB)
            {
                var a = Marshal.PtrToStringUni(strA, lenA / 2);
                var b = Marshal.PtrToStringUni(strB, lenB / 2);
                return Comparer.Compare(a, b);
            }
        }

        public virtual void UnsetCollationFunction(string name) => SetCollationFunction(name, null);

        public SQLiteTokenizer GetSimpleTokenizer(params string[] arguments) => GetTokenizer("simple", arguments);
        public SQLiteTokenizer GetPorterTokenizer(params string[] arguments) => GetTokenizer("porter", arguments);
        public SQLiteTokenizer GetUnicodeTokenizer(params string[] arguments) => GetTokenizer("unicode61", arguments);

        // https://www.sqlite.org/fts3.html#tokenizer
        // we use FTS3/4 because FTS5 is not included in winsqlite.dll (as of today 2019/1/15)
        public SQLiteTokenizer GetTokenizer(string name, params string[] arguments)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            // try a managed one first
            _tokenizers.TryGetValue(name, out var existing);
            if (existing != null)
                return existing;

            // this presumes native library is compiled with fts3 or higher support
            var bytes = ExecuteScalar<byte[]>("SELECT fts3_tokenizer(?)", name);
            if (bytes == null)
                return null;

            IntPtr ptr;
            if (IntPtr.Size == 8)
            {
                var addr = BitConverter.ToInt64(bytes, 0);
                ptr = new IntPtr(addr);
            }
            else
            {
                var addr = BitConverter.ToInt32(bytes, 0);
                ptr = new IntPtr(addr);
            }

            return new NativeTokenizer(this, name, ptr, arguments);
        }

        // note we cannot remove a tokenizer
        public void SetTokenizer(SQLiteTokenizer tokenizer)
        {
            if (tokenizer == null)
                throw new ArgumentNullException(nameof(tokenizer));

            if (tokenizer is NativeTokenizer) // the famous bonehead check
                throw new ArgumentException(null, nameof(tokenizer));

            if (_tokenizers.ContainsKey(tokenizer.Name))
                throw new ArgumentException(null, nameof(tokenizer));

            var mt = new sqlite3_tokenizer_module();
            tokenizer._module = GCHandle.Alloc(mt, GCHandleType.Pinned);
            _tokenizers.AddOrUpdate(tokenizer.Name, tokenizer, (k, old) => tokenizer);

            xCreate dcreate = (int c, string[] args, out IntPtr p) =>
            {
                p = Marshal.AllocCoTaskMem(IntPtr.Size);
                return SQLiteErrorCode.SQLITE_OK;
            };
            tokenizer._create = GCHandle.Alloc(dcreate);
            mt.xCreate = Marshal.GetFunctionPointerForDelegate(dcreate);

            xDestroy ddestroy = (p) =>
            {
                Marshal.FreeCoTaskMem(p);
                return SQLiteErrorCode.SQLITE_OK;
            };
            tokenizer._destroy = GCHandle.Alloc(ddestroy);
            mt.xDestroy = Marshal.GetFunctionPointerForDelegate(ddestroy);

            xOpen dopen = (IntPtr pTokenizer, IntPtr pInput, int nBytes, out IntPtr ppCursor) =>
            {
                if (nBytes < 0)
                {
                    // find terminating zero
                    nBytes = 0;
                    do
                    {
                        var b = Marshal.ReadByte(pInput + nBytes);
                        if (b == 0)
                            break;

                        nBytes++;
                    }
                    while (true);
                }

                var bytes = new byte[nBytes];
                Marshal.Copy(pInput, bytes, 0, bytes.Length);
                var input = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
                var enumerable = tokenizer.Tokenize(input);
                if (enumerable == null)
                {
                    ppCursor = IntPtr.Zero;
                    return SQLiteErrorCode.SQLITE_ERROR;
                }

                var enumerator = enumerable.GetEnumerator();
                if (enumerator == null)
                {
                    ppCursor = IntPtr.Zero;
                    return SQLiteErrorCode.SQLITE_ERROR;
                }

                var te = new TokenEnumerator();
                te.Tokenizer = IntPtr.Zero;
                te.Address = Marshal.AllocCoTaskMem(Marshal.SizeOf<TokenEnumerator>());
                TokenEnumerator._enumerators[te.Address] = enumerator;
                Marshal.StructureToPtr(te, te.Address, false);
                ppCursor = te.Address;
                return SQLiteErrorCode.SQLITE_OK;
            };
            tokenizer._open = GCHandle.Alloc(dopen);
            mt.xOpen = Marshal.GetFunctionPointerForDelegate(dopen);

            xClose dclose = (p) =>
            {
                var te = Marshal.PtrToStructure<TokenEnumerator>(p);
                TokenEnumerator._enumerators.TryRemove(te.Address, out var kv);
                if (te.Token != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(te.Token);
                    te.Token = IntPtr.Zero;
                }
                Marshal.FreeCoTaskMem(te.Address);
                te.Address = IntPtr.Zero;
                return SQLiteErrorCode.SQLITE_OK;
            };
            tokenizer._close = GCHandle.Alloc(dclose);
            mt.xClose = Marshal.GetFunctionPointerForDelegate(dclose);

            xNext dnext = (IntPtr pCursor, out IntPtr ppToken, out int pnBytes, out int piStartOffset, out int piEndOffset, out int piPosition) =>
            {
                ppToken = IntPtr.Zero;
                pnBytes = 0;
                piStartOffset = 0;
                piEndOffset = 0;
                piPosition = 0;

                var te = Marshal.PtrToStructure<TokenEnumerator>(pCursor);
                if (te.Token != IntPtr.Zero)
                {
                    // from sqlite3.c
                    //** The buffer *ppToken is set to point at is managed by the tokenizer
                    //** implementation. It is only required to be valid until the next call
                    //** to xNext() or xClose(). 
                    Marshal.FreeCoTaskMem(te.Token);
                    Marshal.WriteIntPtr(pCursor + 2 * IntPtr.Size, IntPtr.Zero); // offset of TokenEnumerator.Token
                }

                if (!te.Enumerator.MoveNext())
                    return SQLiteErrorCode.SQLITE_DONE;

                var token = te.Enumerator.Current;
                if (token == null || token.Text == null)
                    return SQLiteErrorCode.SQLITE_ERROR;

                var bytes = Encoding.UTF8.GetBytes(token.Text);
                ppToken = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.WriteIntPtr(pCursor + 2 * IntPtr.Size, ppToken); // offset of TokenEnumerator.Token
                Marshal.Copy(bytes, 0, ppToken, bytes.Length);
                pnBytes = bytes.Length;
                piStartOffset = token.StartOffset;
                piEndOffset = token.EndOffset;
                piPosition = token.Position;
                return SQLiteErrorCode.SQLITE_OK;
            };
            tokenizer._next = GCHandle.Alloc(dnext);
            mt.xNext = Marshal.GetFunctionPointerForDelegate(dnext);

            xLanguageid dlangid = (p, i) => SQLiteErrorCode.SQLITE_OK;
            tokenizer._languageid = GCHandle.Alloc(dlangid);
            mt.xLanguageid = Marshal.GetFunctionPointerForDelegate(dlangid);

            // we need to copy struct's data again as structs are copied when pinned
            Marshal.StructureToPtr(mt, tokenizer._module.AddrOfPinnedObject(), false);

            byte[] blob;
            if (IntPtr.Size == 8)
            {
                blob = BitConverter.GetBytes((long)tokenizer._module.AddrOfPinnedObject());
            }
            else
            {
                blob = BitConverter.GetBytes((int)tokenizer._module.AddrOfPinnedObject());
            }
            ExecuteNonQuery("SELECT fts3_tokenizer(?, ?)", tokenizer.Name, blob);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenEnumerator
        {
            // we *need* this because it's overwritten somehow by the FTS...
            public IntPtr Tokenizer;
            public IntPtr Address;

            // don't change this order as we rewrite Token at this precise offset (2 * IntPtr.Size)
            public IntPtr Token;

            // is there another smart way than to use a dic?
            public static readonly ConcurrentDictionary<IntPtr, IEnumerator<SQLiteToken>> _enumerators = new ConcurrentDictionary<IntPtr, IEnumerator<SQLiteToken>>();
            public IEnumerator<SQLiteToken> Enumerator => _enumerators[Address];
        }

        private sealed class NativeTokenizer : SQLiteTokenizer
        {
            private readonly xDestroy _destroyFn;
            private readonly xClose _closeFn;
            private readonly xOpen _openFn;
            private readonly xNext _nextFn;
            //private readonly xLanguageid _languageidFn;
            private readonly IntPtr _tokenizer;
            private int _disposed;

            public NativeTokenizer(SQLiteDatabase database, string name, IntPtr ptr, params string[] arguments)
                : base(database, name)
            {
                var module = Marshal.PtrToStructure<sqlite3_tokenizer_module>(ptr);
                Version = module.iVersion;
                var create = Marshal.GetDelegateForFunctionPointer<xCreate>(module.xCreate);
                _destroyFn = Marshal.GetDelegateForFunctionPointer<xDestroy>(module.xDestroy);
                _openFn = Marshal.GetDelegateForFunctionPointer<xOpen>(module.xOpen);
                _closeFn = Marshal.GetDelegateForFunctionPointer<xClose>(module.xClose);
                _nextFn = Marshal.GetDelegateForFunctionPointer<xNext>(module.xNext);
                //_languageidFn = module.xLanguageid != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<xLanguageid>(module.xLanguageid) : null;
                var argc = (arguments?.Length).GetValueOrDefault();
                Database.CheckError(create(argc, arguments, out _tokenizer));
            }

            public override IEnumerable<SQLiteToken> Tokenize(string input)
            {
                if (string.IsNullOrWhiteSpace(input))
                    yield break;

                var bytes = Encoding.UTF8.GetBytes(input + '\0');
                var ptr = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                try
                {
                    Database.CheckError(_openFn(_tokenizer, ptr, bytes.Length, out var cursor));

                    // this is weird but, as we can see in sqlite3.c unicodeOpen implementation,
                    // the tokenizer is not copied to the cursor so we do it ourselves...
                    Marshal.WriteIntPtr(cursor, _tokenizer);

                    try
                    {
                        do
                        {
                            var error = _nextFn(cursor, out var token, out var len, out var startOffset, out var endOffset, out var position);
                            if (error == SQLiteErrorCode.SQLITE_DONE)
                                yield break;

                            var sbytes = new byte[len];
                            Marshal.Copy(token, sbytes, 0, len);
                            var text = Encoding.UTF8.GetString(sbytes);
                            yield return new SQLiteToken(text, startOffset, endOffset, position);
                        }
                        while (true);
                    }
                    finally
                    {
                        _closeFn(cursor);
                    }
                }
                finally
                {
                    Utf8Marshaler.Instance.CleanUpNativeData(ptr);
                }
            }

            protected override void Dispose(bool disposing)
            {
                var disposed = Interlocked.Exchange(ref _disposed, 1);
                if (disposed != 0)
                    return;

                if (disposing)
                {
#if DEBUG
                    Database.CheckError(_destroyFn(_tokenizer), true);
#else
                    Database.CheckError(_destroyFn(_tokenizer), false);
#endif
                }

                base.Dispose(disposing);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
#pragma warning disable IDE1006 // Naming Styles
        private struct sqlite3_tokenizer_module
#pragma warning restore IDE1006 // Naming Styles
        {
            public int iVersion;
            public IntPtr xCreate;
            public IntPtr xDestroy;
            public IntPtr xOpen;
            public IntPtr xClose;
            public IntPtr xNext;
            public IntPtr xLanguageid;
        }

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        // note: this supports only ANSI, not UTF8 really, but let's say that's ok for arguments...
        private delegate SQLiteErrorCode xCreate(int argc, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] string[] argv, out IntPtr ppTokenizer);

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode xDestroy(IntPtr pTokenizer);

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode xOpen(IntPtr pTokenizer, IntPtr pInput, int nBytes, out IntPtr ppCursor);

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode xClose(IntPtr pCursor);

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode xNext(IntPtr pCursor, out IntPtr ppToken, out int pnBytes, out int piStartOffset, out int piEndOffset, out int piPosition);

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode xLanguageid(IntPtr pCursor, int iLangid);

        public virtual void SetScalarFunction(string name, int argumentsCount, bool deterministic, Action<SQLiteFunctionContext> function)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            var enc = SQLiteTextEncoding.SQLITE_UTF16;
            if (deterministic)
            {
                enc |= SQLiteTextEncoding.SQLITE_DETERMINISTIC;
            }

            // a function is defined by the unique combination of name+argc+encoding
            var key = name + "\0" + argumentsCount + "\0" + (int)enc;
            if (function == null)
            {
                CheckError(_sqlite3_create_function16(CheckDisposed(), name, argumentsCount, SQLiteTextEncoding.SQLITE_UTF16, IntPtr.Zero, null, null, null));
                _functionSinks.TryRemove(key, out _);
                return;
            }

            var sink = new ScalarFunctionSink();
            sink.Database = this;
            sink.Function = function;
            sink.Name = name;
            _functionSinks[key] = sink;

            CheckError(_sqlite3_create_function16(CheckDisposed(), name, argumentsCount, SQLiteTextEncoding.SQLITE_UTF16, IntPtr.Zero, sink.Callback, null, null));
        }

        private sealed class ScalarFunctionSink
        {
            public Action<SQLiteFunctionContext> Function;
            public SQLiteDatabase Database;
            public string Name;
            public xFunc Callback;

            public ScalarFunctionSink()
            {
                Callback = new xFunc(Call);
            }

            public void Call(IntPtr context, int argsCount, IntPtr[] args)
            {
                var ctx = new SQLiteFunctionContext(Database, context, Name, argsCount, args);
                Function(ctx);
            }
        }

        public virtual void UnsetScalarFunction(string name, int argumentsCount) => SetScalarFunction(name, argumentsCount, true, null);

        public void LogInfo(object value, [CallerMemberName] string methodName = null) => Log(TraceLevel.Info, value, methodName);
        public virtual void Log(TraceLevel level, object value, [CallerMemberName] string methodName = null) => Logger?.Log(level, value, methodName);

        public virtual void ShrinkMemory() => ExecuteNonQuery("PRAGMA shrink_memory");
        public virtual void Vacuum() => ExecuteNonQuery("VACUUM");
        public virtual void CacheFlush() => CheckError(_sqlite3_db_cacheflush(CheckDisposed()));

        public bool CheckIntegrity() => CheckIntegrity(100).FirstOrDefault().EqualsIgnoreCase("ok");
        public IEnumerable<string> CheckIntegrity(int maximumErrors) => LoadObjects("PRAGMA integrity_check(" + maximumErrors + ")").Select(o => (string)o[0]);

        public void EnableLoadExtension(bool enable, bool throwOnError = true) => CheckError(_sqlite3_enable_load_extension(Handle, enable ? 1 : 0), throwOnError);

        public void LoadExtension(string fileName, string procedure = null, bool throwOnError = true)
        {
            var code = _sqlite3_load_extension(Handle, fileName, procedure, out var ptr);
            if (code == SQLiteErrorCode.SQLITE_OK)
                return;

            string msg = null;
            if (ptr != IntPtr.Zero)
            {
                msg = Marshal.PtrToStringAnsi(ptr).Nullify();
            }

            var ex = msg != null ? new SQLiteException(code, msg) : new SQLiteException(code);
            Log(TraceLevel.Error, ex.Message, nameof(LoadExtension));
            if (throwOnError)
                throw ex;
        }

        public static void EnableSharedCache(bool enable, bool throwOnError = true)
        {
            HookNativeProcs();
            StaticCheckError(_sqlite3_enable_shared_cache(enable ? 1 : 0), throwOnError);
        }

        public static void Configure(SQLiteConfiguration configuration, bool throwOnError = true, params object[] arguments)
        {
            HookNativeProcs();
            switch (configuration)
            {
                case SQLiteConfiguration.SQLITE_CONFIG_SINGLETHREAD:
                case SQLiteConfiguration.SQLITE_CONFIG_MULTITHREAD:
                case SQLiteConfiguration.SQLITE_CONFIG_SERIALIZED:
                    StaticCheckError(_sqlite3_config_0(configuration), throwOnError);
                    break;

                case SQLiteConfiguration.SQLITE_CONFIG_MEMSTATUS:
                case SQLiteConfiguration.SQLITE_CONFIG_COVERING_INDEX_SCAN:
                case SQLiteConfiguration.SQLITE_CONFIG_URI:
                case SQLiteConfiguration.SQLITE_CONFIG_STMTJRNL_SPILL:
                case SQLiteConfiguration.SQLITE_CONFIG_SORTERREF_SIZE:
                case SQLiteConfiguration.SQLITE_CONFIG_WIN32_HEAPSIZE:
                case SQLiteConfiguration.SQLITE_CONFIG_SMALL_MALLOC:
                    if (arguments == null)
                        throw new ArgumentNullException(nameof(arguments));

                    Check1(arguments);
                    StaticCheckError(_sqlite3_config_2(configuration, Conversions.ChangeType<int>(arguments[0])), throwOnError);
                    break;

                case SQLiteConfiguration.SQLITE_CONFIG_LOOKASIDE:
                    if (arguments == null)
                        throw new ArgumentNullException(nameof(arguments));

                    Check2(arguments);
                    StaticCheckError(_sqlite3_config_4(configuration, Conversions.ChangeType<int>(arguments[0]), Conversions.ChangeType<int>(arguments[1])), throwOnError);
                    break;

                case SQLiteConfiguration.SQLITE_CONFIG_MMAP_SIZE:
                    if (arguments == null)
                        throw new ArgumentNullException(nameof(arguments));

                    Check2(arguments);
                    StaticCheckError(_sqlite3_config_3(configuration, Conversions.ChangeType<long>(arguments[0]), Conversions.ChangeType<long>(arguments[1])), throwOnError);
                    break;

                case SQLiteConfiguration.SQLITE_CONFIG_MEMDB_MAXSIZE:
                    if (arguments == null)
                        throw new ArgumentNullException(nameof(arguments));

                    Check1(arguments);
                    StaticCheckError(_sqlite3_config_1(configuration, Conversions.ChangeType<long>(arguments[0])), throwOnError);
                    break;

                default:
                    throw new NotSupportedException();
            }
        }

        public virtual object Configure(SQLiteDatabaseConfiguration configuration, bool throwOnError = true, params object[] arguments)
        {
            if (arguments == null)
                throw new ArgumentNullException(nameof(arguments));

            int result;
            switch (configuration)
            {
                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_ENABLE_FKEY:
                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_ENABLE_TRIGGER:
                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_ENABLE_FTS3_TOKENIZER:
                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION:
                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE:
                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_ENABLE_QPSG:
                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_TRIGGER_EQP:
                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_RESET_DATABASE:
                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_DEFENSIVE:
                    Check1(arguments);
                    CheckError(_sqlite3_db_config_0(CheckDisposed(), configuration, Conversions.ChangeType<int>(arguments[0]), out result), throwOnError);
                    return result;

                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_LOOKASIDE:
                    Check3(arguments);
                    CheckError(_sqlite3_db_config_1(CheckDisposed(), configuration, Conversions.ChangeType<IntPtr>(arguments[0]), Conversions.ChangeType<int>(arguments[1]), Conversions.ChangeType<int>(arguments[2])), throwOnError);
                    return null;

                case SQLiteDatabaseConfiguration.SQLITE_DBCONFIG_MAINDBNAME:
                    Check1(arguments);
                    CheckError(_sqlite3_db_config_2(CheckDisposed(), configuration, Conversions.ChangeType<string>(arguments[0])), throwOnError);
                    return null;

                default:
                    throw new NotSupportedException();
            }

        }

        static void Check1(object[] arguments)
        {
            if (arguments.Length != 1)
                throw new ArgumentException(null, nameof(arguments));
        }

        static void Check2(object[] arguments)
        {
            if (arguments.Length != 2)
                throw new ArgumentException(null, nameof(arguments));
        }

        static void Check3(object[] arguments)
        {
            if (arguments.Length != 3)
                throw new ArgumentException(null, nameof(arguments));
        }

        public SQLiteTable GetTable<T>() => GetObjectTable<T>()?.Table;
        public SQLiteTable GetTable(Type type) => GetObjectTable(type)?.Table;
        public SQLiteTable GetTable(string name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            return Tables.FirstOrDefault(t => name.EqualsIgnoreCase(t.Name));
        }

        public SQLiteObjectTable SynchronizeIndices<T>(SQLiteSaveOptions options = null) => SynchronizeIndices(typeof(T), options);
        public virtual SQLiteObjectTable SynchronizeIndices(Type type, SQLiteSaveOptions options = null)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var table = GetObjectTable(type, options?.BuildTableOptions);
            if (table == null)
                throw new InvalidOperationException();

            table.SynchronizeIndices(options);
            return table;
        }

        public SQLiteObjectTable SynchronizeSchema<T>(SQLiteSaveOptions options = null) => SynchronizeSchema(typeof(T), options);
        public virtual SQLiteObjectTable SynchronizeSchema(Type type, SQLiteSaveOptions options = null)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var table = GetObjectTable(type, options?.BuildTableOptions);
            if (table == null)
                throw new InvalidOperationException();

            table.SynchronizeSchema(options);
            return table;
        }

        public void DeleteTable<T>(bool throwOnError = true) => DeleteTable(typeof(T), throwOnError);
        public virtual void DeleteTable(Type type, bool throwOnError = true) => DeleteTable(GetObjectTable(type).Name, throwOnError);
        public virtual void DeleteTable(string name, bool throwOnError = true)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            if (throwOnError)
            {
                ExecuteNonQuery("DROP TABLE IF EXISTS " + SQLiteStatement.EscapeName(name));
            }
            else
            {
                try
                {
                    ExecuteNonQuery("DROP TABLE IF EXISTS " + SQLiteStatement.EscapeName(name));
                }
                catch (Exception e)
                {
                    Log(TraceLevel.Warning, "Error trying to delete TABLE '" + name + "': " + e);
                }
            }
        }

        public virtual void DeleteTempTables(bool throwOnError = true)
        {
            foreach (var table in Tables.Where(t => t.Name.StartsWith(SQLiteObjectTable._tempTablePrefix, StringComparison.Ordinal)).ToArray())
            {
                table.Delete(throwOnError);
            }
        }

        public bool TableExists<T>() => TableExists(typeof(T));
        public virtual bool TableExists(Type objectType) => TableExists(GetObjectTable(objectType).Name);
        public virtual bool TableExists(string name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            return ExecuteScalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?1 COLLATE NOCASE LIMIT 1", 0, name) > 0;
        }

        public virtual object CoerceValueForBind(object value, SQLiteBindOptions bindOptions = null)
        {
            if (value == null || Convert.IsDBNull(value))
                return null;

            if (value is ISQLiteObject so)
            {
                var pk = so.GetPrimaryKey();
                value = pk;
                if (pk != null)
                {
                    if (pk.Length == 0)
                    {
                        value = null;
                    }
                    else if (pk.Length == 1)
                    {
                        value = CoerceValueForBind(pk[0], bindOptions);
                    }
                    else // > 1
                    {
                        value = string.Join(PrimaryKeyPersistenceSeparator, pk);
                    }
                }
            }

            var type = GetBindType(value);
            var ctx = CreateBindContext();
            if (ctx == null)
                throw new InvalidOperationException();

            if (bindOptions != null)
            {
                ctx.Options = bindOptions;
            }

            ctx.Value = value;
            return type.ConvertFunc(ctx);
        }

        private static Type GetObjectType(object obj)
        {
            if (obj == null)
                return typeof(DBNull);

            if (obj is Type type)
                return type;

            return obj.GetType();
        }

        public SQLiteBindType GetBindType(object obj) => GetBindType(GetObjectType(obj), null);
        public SQLiteBindType GetBindType(object obj, SQLiteBindType defaultType) => GetBindType(GetObjectType(obj), defaultType);

        public SQLiteBindType GetBindType(Type type) => GetBindType(type, null);
        public virtual SQLiteBindType GetBindType(Type type, SQLiteBindType defaultType)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            if (_bindTypes.TryGetValue(type, out var bindType) && bindType != null)
                return bindType;

            if (type.IsEnum && !BindOptions.EnumAsString)
            {
                var et = GetEnumBindType(type);
                return _bindTypes.AddOrUpdate(type, et, (k, o) => et);
            }

            foreach (var kv in _bindTypes)
            {
                if (kv.Key == typeof(object))
                    continue;

                if (kv.Key.IsAssignableFrom(type))
                    return _bindTypes.AddOrUpdate(type, kv.Value, (k, o) => o);
            }

            return defaultType ?? SQLiteBindType.ObjectToStringType;
        }

        public virtual SQLiteBindType GetEnumBindType(Type enumType)
        {
            if (!enumType.IsEnum)
                throw new ArgumentException(null, nameof(enumType));

            var ut = Enum.GetUnderlyingType(enumType);
            var type = new SQLiteBindType(ctx => Convert.ChangeType(ctx.Value, ut), enumType);
            return type;
        }

        public virtual void AddBindType(SQLiteBindType type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            foreach (var handledType in type.HandledClrTypes)
            {
                _bindTypes.AddOrUpdate(handledType, type, (k, o) => type);
            }
        }

        public virtual SQLiteBindType RemoveBindType(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            _bindTypes.TryRemove(type, out var value);
            return value;
        }

        public virtual void ClearBindTypes() => _bindTypes.Clear();

        protected virtual void AddDefaultBindTypes()
        {
            AddBindType(SQLiteBindType.ByteType);
            AddBindType(SQLiteBindType.DateTimeType);
            AddBindType(SQLiteBindType.DBNullType);
            AddBindType(SQLiteBindType.DecimalType);
            AddBindType(SQLiteBindType.FloatType);
            AddBindType(SQLiteBindType.GuidType);
            AddBindType(SQLiteBindType.Int16Type);
            AddBindType(SQLiteBindType.ObjectToStringType);
            AddBindType(SQLiteBindType.PassThroughType);
            AddBindType(SQLiteBindType.SByteType);
            AddBindType(SQLiteBindType.TimeSpanType);
            AddBindType(SQLiteBindType.UInt16Type);
            AddBindType(SQLiteBindType.UInt32Type);
            AddBindType(SQLiteBindType.UInt64Type);
            AddBindType(SQLiteBindType.PassThroughType);
        }

        public virtual int DeleteAll<T>()
        {
            var table = GetObjectTable(typeof(T));
            if (table == null)
                return 0;

            return DeleteAll(table.Name);
        }

        public virtual int DeleteAll(string tableName)
        {
            if (tableName == null)
                throw new ArgumentNullException(nameof(tableName));

            var sql = "DELETE FROM " + SQLiteStatement.EscapeName(tableName);
            return ExecuteNonQuery(sql);
        }

        public virtual bool Delete(object obj, SQLiteDeleteOptions options = null)
        {
            if (obj == null)
                return false;

            var table = GetObjectTable(obj.GetType());
            if (table == null)
                return false;

            if (!table.HasPrimaryKey)
                throw new SqlNadoException("0008: Cannot delete object from table '" + table.Name + "' as it does not define a primary key.");

            var pk = table.PrimaryKeyColumns.Select(c => c.GetValueForBind(obj)).ToArray();
            if (pk == null)
                throw new InvalidOperationException();

            var sql = "DELETE FROM " + table.EscapedName + " WHERE " + table.BuildWherePrimaryKeyStatement();
            return ExecuteNonQuery(sql, pk) > 0;
        }

        public int Count<T>() => Count(typeof(T));
        public virtual int Count(Type objectType)
        {
            if (objectType == null)
                throw new ArgumentNullException(nameof(objectType));

            var table = GetObjectTable(objectType);
            if (table == null)
                return 0;

            return ExecuteScalar("SELECT count(*) FROM " + table.EscapedName, 0);
        }

        public virtual int Save<T>(IEnumerable<T> enumerable, SQLiteSaveOptions options = null) => Save((IEnumerable)enumerable, options);
        public virtual int Save(IEnumerable enumerable, SQLiteSaveOptions options = null)
        {
            if (enumerable == null)
                return 0;

            if (options == null)
            {
                options = CreateSaveOptions();
                if (options == null)
                    throw new InvalidOperationException();

                options.UseSavePoint = true;
                options.SynchronizeSchema = true;
                options.SynchronizeIndices = true;
            }

            var count = 0;
            var i = 0;
            try
            {
                foreach (var obj in enumerable)
                {
                    options.Index = i;
                    if (i == 0)
                    {
                        if (options.UseSavePoint)
                        {
                            options.SavePointName = "_sp" + Guid.NewGuid().ToString("N");
                            ExecuteNonQuery("SAVEPOINT " + options.SavePointName);
                        }
                        else if (options.UseTransaction)
                        {
                            ExecuteNonQuery("BEGIN TRANSACTION");
                        }
                    }
                    else
                    {
                        options.SynchronizeSchema = false;
                        options.SynchronizeIndices = false;
                    }

                    if (Save(obj, options))
                    {
                        count++;
                    }

                    i++;
                }
            }
            catch
            {
                options.Index = -1;
                if (options.SavePointName != null)
                {
                    ExecuteNonQuery("ROLLBACK TO " + options.SavePointName);
                    options.SavePointName = null;
                }
                else if (options.UseTransaction)
                {
                    ExecuteNonQuery("ROLLBACK");
                }
                throw;
            }

            options.Index = -1;
            if (options.SavePointName != null)
            {
                ExecuteNonQuery("RELEASE " + options.SavePointName);
                options.SavePointName = null;
            }
            else if (options.UseTransaction)
            {
                ExecuteNonQuery("COMMIT");
            }
            return count;
        }

        public virtual T RunSavePoint<T>(Func<T> action, string name = null)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            if (string.IsNullOrWhiteSpace(name))
            {
                name = "_sp" + Guid.NewGuid().ToString("N");
            }

            ExecuteNonQuery("SAVEPOINT " + name);
            try
            {
                var result = action();
                ExecuteNonQuery("RELEASE " + name);
                return result;
            }
            catch
            {
                ExecuteNonQuery("ROLLBACK TO " + name);
                throw;
            }
        }

        public virtual void RunSavePoint(Action action, string name = null)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            if (string.IsNullOrWhiteSpace(name))
            {
                name = "_sp" + Guid.NewGuid().ToString("N");
            }

            ExecuteNonQuery("SAVEPOINT " + name);
            try
            {
                action();
                ExecuteNonQuery("RELEASE " + name);
            }
            catch
            {
                ExecuteNonQuery("ROLLBACK TO " + name);
                throw;
            }
        }

        public virtual T RunTransaction<T>(Func<T> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            BeginTransaction();
            try
            {
                var result = action();
                Commit();
                return result;
            }
            catch
            {
                Rollback();
                throw;
            }
        }

        public virtual void RunTransaction(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            BeginTransaction();
            try
            {
                action();
                Commit();
            }
            catch
            {
                Rollback();
                throw;
            }
        }

        public virtual void BeginTransaction(SQLiteTransactionType type = SQLiteTransactionType.Deferred)
        {
            switch (type)
            {
                case SQLiteTransactionType.Exclusive:
                    ExecuteNonQuery("BEGIN EXCLUSIVE TRANSACTION");
                    break;

                case SQLiteTransactionType.Immediate:
                    ExecuteNonQuery("BEGIN IMMEDIATE TRANSACTION");
                    break;

                case SQLiteTransactionType.Deferred:
                    ExecuteNonQuery("BEGIN TRANSACTION");
                    break;

                default:
                    throw new NotSupportedException();
            }
        }

        public virtual void Commit() => ExecuteNonQuery("COMMIT");
        public virtual void Rollback() => ExecuteNonQuery("ROLLBACK");

        public virtual bool Save(object obj, SQLiteSaveOptions options = null)
        {
            if (obj == null)
                return false;

            if (options == null)
            {
                options = CreateSaveOptions();
                if (options == null)
                    throw new InvalidOperationException();

                options.SynchronizeSchema = true;
                options.SynchronizeIndices = true;
            }

            var table = GetObjectTable(obj.GetType(), options.BuildTableOptions);
            if (table == null)
                throw new InvalidOperationException();

            if (options.SynchronizeSchema)
            {
                table.SynchronizeSchema(options);
            }

            return table.Save(obj, options);
        }

        public virtual IEnumerable<T> LoadByForeignKey<T>(object instance, SQLiteLoadForeignKeyOptions options = null)
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            var instanceTable = GetObjectTable(instance.GetType());
            if (instanceTable == null)
                yield break;

            if (!instanceTable.HasPrimaryKey)
                throw new SqlNadoException("0013: Table '" + instanceTable.Name + "' has no primary key.", new ArgumentException(null, nameof(instance)));

            var table = GetObjectTable(typeof(T));
            if (table == null)
                yield break;

            if (table.LoadAction == null)
                throw new SqlNadoException("0014: Table '" + table.Name + "' does not define a LoadAction.");

            options = options ?? CreateLoadForeignKeyOptions();
            if (options == null)
                throw new InvalidOperationException();

            var fkCol = options.ForeignKeyColumn;
            if (fkCol == null)
            {
                if (options.ForeignKeyColumnName != null)
                {
                    fkCol = table.Columns.FirstOrDefault(c => c.Name.EqualsIgnoreCase(options.ForeignKeyColumnName));
                    if (fkCol == null)
                        throw new SqlNadoException("0015: Foreign key column '" + options.ForeignKeyColumnName + "' was not found on table '" + table.Name + "'.");
                }
                else
                {
                    fkCol = table.Columns.FirstOrDefault(c => c.ClrType == instance.GetType());
                    if (fkCol == null)
                        throw new SqlNadoException("0016: Foreign key column for table '" + instanceTable.Name + "' was not found on table '" + table.Name + "'.");
                }
            }

            var pk = instanceTable.GetPrimaryKey(instance);
            var sql = "SELECT ";
            if (options.RemoveDuplicates)
            {
                sql += "DISTINCT ";
            }
            sql += table.BuildColumnsStatement() + " FROM " + table.EscapedName + " WHERE " + fkCol.EscapedName + "=?";

            var setProp = options.SetForeignKeyPropertyValue && fkCol.SetValueAction != null;
            foreach (var obj in Load<T>(sql, options, pk))
            {
                if (setProp)
                {
                    fkCol.SetValue(options, obj, instance);
                }
                yield return obj;
            }
        }

        public IEnumerable<SQLiteRow> GetTableRows<T>() => GetTableRows<T>(int.MaxValue);
        public virtual IEnumerable<SQLiteRow> GetTableRows<T>(int maximumRows) => GetTableRows(GetObjectTable(typeof(T)).Name, maximumRows);

        public IEnumerable<SQLiteRow> GetTableRows(string tableName) => GetTableRows(tableName, int.MaxValue);
        public virtual IEnumerable<SQLiteRow> GetTableRows(string tableName, int maximumRows)
        {
            if (tableName == null)
                throw new ArgumentNullException(nameof(tableName));

            var sql = "SELECT * FROM " + SQLiteStatement.EscapeName(tableName);
            if (maximumRows > 0 && maximumRows < int.MaxValue)
            {
                sql += " LIMIT " + maximumRows;
            }
            return LoadRows(sql);
        }

        public IEnumerable<T> LoadAll<T>(int maximumRows)
        {
            var options = CreateLoadOptions();
            if (options == null)
                throw new InvalidOperationException();

            options.MaximumRows = maximumRows;
            return Load<T>(null, options);
        }

        public IEnumerable<T> LoadAll<T>(SQLiteLoadOptions options = null) => Load<T>(null, options);
        public IEnumerable<T> Load<T>(string sql, params object[] args) => Load<T>(sql, null, args);
        public virtual IEnumerable<T> Load<T>(string sql, SQLiteLoadOptions options, params object[] args)
        {
            var table = GetObjectTable(typeof(T));
            if (table == null)
                yield break;

            if (table.LoadAction == null)
                throw new SqlNadoException("0009: Table '" + table.Name + "' does not define a LoadAction.");

            sql = sql.Nullify();
            if (sql == null || sql.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                var newsql = "SELECT ";
                if (options?.RemoveDuplicates == true)
                {
                    newsql += "DISTINCT ";
                }

                newsql += table.BuildColumnsStatement() + " FROM " + table.EscapedName;
                if (sql != null)
                {
                    if (sql.Length > 0 && sql[0] != ' ')
                    {
                        newsql += " ";
                    }
                    newsql += sql;
                }
                sql = newsql;
            }

            options = options ?? CreateLoadOptions();
            if (options == null)
                throw new InvalidOperationException();

            if (options.TestTableExists && !TableExists<T>())
                yield break;

            if (options.Limit > 0 || options.Offset > 0)
            {
                var limit = options.Limit;
                if (limit <= 0)
                {
                    limit = -1;
                }

                sql += " LIMIT " + limit;
                if (options.Offset > 0)
                {
                    sql += " OFFSET " + options.Offset;
                }
            }

            using (var statement = PrepareStatement(sql, options.ErrorHandler, args))
            {
                var index = 0;
                do
                {
                    var code = _sqlite3_step(statement.CheckDisposed());
                    if (code == SQLiteErrorCode.SQLITE_DONE)
                    {
                        index++;
                        Log(TraceLevel.Verbose, "Step done at index " + index + " for `" + sql + "`");
                        break;
                    }

                    if (code == SQLiteErrorCode.SQLITE_ROW)
                    {
                        index++;
                        var obj = table.Load<T>(statement, options);
                        if (obj != null)
                            yield return obj;

                        if (options.MaximumRows > 0 && index >= options.MaximumRows)
                        {
                            Log(TraceLevel.Verbose, "Step break at index " + index + " for `" + sql + "`");
                            break;
                        }

                        continue;
                    }

                    var errorHandler = options.ErrorHandler;
                    if (errorHandler != null)
                    {
                        var error = new SQLiteError(statement, index, code);
                        var action = errorHandler(error);
                        index = error.Index;
                        code = error.Code;
                        if (action == SQLiteOnErrorAction.Break)
                            break;

                        if (action == SQLiteOnErrorAction.Continue)
                        {
                            index++;
                            continue;
                        }

                        // else throw
                    }

                    CheckError(code, sql: sql);
                }
                while (true);
            }
        }

        public T LoadByPrimaryKeyOrCreate<T>(object key, SQLiteLoadOptions options = null) => (T)LoadByPrimaryKeyOrCreate(typeof(T), key, options);
        public virtual object LoadByPrimaryKeyOrCreate(Type objectType, object key, SQLiteLoadOptions options = null)
        {
            options = options ?? CreateLoadOptions();
            if (options == null)
                throw new InvalidOperationException();

            options.CreateIfNotLoaded = true;
            return LoadByPrimaryKey(objectType, key, options);
        }

        public virtual T LoadByPrimaryKey<T>(object key, SQLiteLoadOptions options = null) => (T)LoadByPrimaryKey(typeof(T), key, options);
        public virtual object LoadByPrimaryKey(Type objectType, object key, SQLiteLoadOptions options = null)
        {
            if (objectType == null)
                throw new ArgumentNullException(nameof(objectType));

            if (key == null)
                throw new ArgumentNullException(nameof(key));

            var table = GetObjectTable(objectType);
            if (table == null)
                return null;

            if (table.LoadAction == null)
                throw new SqlNadoException("0009: Table '" + table.Name + "' does not define a LoadAction.");

            var pk = table.PrimaryKeyColumns.ToArray();
            if (pk.Length == 0)
                throw new SqlNadoException("0025: Table '" + table.Name + "' does not define a primary key.");

            var keys = CoerceToCompositeKey(key);
            if (keys.Length == 0)
                throw new ArgumentException(null, nameof(key));

            if (keys.Length != pk.Length)
                throw new SqlNadoException("0026: Table '" + table.Name + "' primary key has " + pk.Length + " colum(s). Passed composite key contains " + keys.Length + " item(s).");

            if (options == null || !options.DontConvertPrimaryKey)
            {
                for (var i = 0; i < keys.Length; i++)
                {
                    if (keys[i] != null && !pk[i].ClrType.IsAssignableFrom(keys[i].GetType()) && TryChangeType(keys[i], pk[i].ClrType, out object k))
                    {
                        keys[i] = k;
                    }
                }
            }

            var sql = "SELECT";
            if (options?.RemoveDuplicates == true)
            {
                sql += "DISTINCT ";
            }

            sql += "* FROM " + table.EscapedName + " WHERE " + table.BuildWherePrimaryKeyStatement() + " LIMIT 1";
            var obj = Load(objectType, sql, options, keys).FirstOrDefault();
            if (obj == null && (options?.CreateIfNotLoaded).GetValueOrDefault())
            {
                obj = table.GetInstance(objectType, null, options);
                table.SetPrimaryKey(options, obj, keys);
            }
            return obj;
        }

        public virtual object[] CoerceToCompositeKey(object key)
        {
            if (!(key is object[] keys))
            {
                if (key is Array array)
                {
                    keys = new object[array.Length];
                    for (int i = 0; i < keys.Length; i++)
                    {
                        keys[i] = array.GetValue(i);
                    }
                }
                else if (!(key is string) && key is IEnumerable enumerable)
                {
                    keys = enumerable.Cast<object>().ToArray();
                }
                else
                {
                    keys = new object[] { key };
                }
            }
            return keys;
        }

        public virtual SQLiteQuery<T> Query<T>() => new SQLiteQuery<T>(this);
        public virtual SQLiteQuery<T> Query<T>(Expression expression) => new SQLiteQuery<T>(this, expression);

        public IEnumerable<object> LoadAll(Type objectType) => Load(objectType, null, null, null);
        public IEnumerable<object> Load(Type objectType, string sql, params object[] args) => Load(objectType, sql, null, args);
        public virtual IEnumerable<object> Load(Type objectType, string sql, SQLiteLoadOptions options, params object[] args)
        {
            if (objectType == null)
                throw new ArgumentNullException(nameof(objectType));

            var table = GetObjectTable(objectType);
            if (table == null)
                yield break;

            if (table.LoadAction == null)
                throw new SqlNadoException("0024: Table '" + table.Name + "' does not define a LoadAction.");

            if (sql == null)
            {
                sql = "SELECT ";
                if (options?.RemoveDuplicates == true)
                {
                    sql += "DISTINCT ";
                }

                sql += table.BuildColumnsStatement() + " FROM " + table.EscapedName;
            }

            options = options ?? CreateLoadOptions();
            if (options == null)
                throw new InvalidOperationException();

            if (options.TestTableExists && !TableExists(objectType))
                yield break;

            using (var statement = PrepareStatement(sql, options.ErrorHandler, args))
            {
                var index = 0;
                do
                {
                    var code = _sqlite3_step(statement.Handle);
                    if (code == SQLiteErrorCode.SQLITE_DONE)
                    {
                        index++;
                        Log(TraceLevel.Verbose, "Step done at index " + index);
                        break;
                    }

                    if (code == SQLiteErrorCode.SQLITE_ROW)
                    {
                        var obj = table.Load(objectType, statement, options);
                        if (obj != null)
                            yield return obj;

                        index++;
                        continue;
                    }

                    var errorHandler = options.ErrorHandler;
                    if (errorHandler != null)
                    {
                        var error = new SQLiteError(statement, index, code);
                        var action = errorHandler(error);
                        index = error.Index;
                        code = error.Code;
                        if (action == SQLiteOnErrorAction.Break)
                            break;

                        if (action == SQLiteOnErrorAction.Continue)
                        {
                            index++;
                            continue;
                        }

                        // else throw
                    }

                    CheckError(code, sql: sql);
                }
                while (true);
            }
        }

        public T CreateObjectInstance<T>(SQLiteLoadOptions options = null) => (T)CreateObjectInstance(typeof(T), options);
        public virtual object CreateObjectInstance(Type objectType, SQLiteLoadOptions options = null)
        {
            if (objectType == null)
                throw new ArgumentNullException(nameof(objectType));

            var table = GetObjectTable(objectType);
            if (table == null)
                throw new InvalidOperationException();

            return table.GetInstance(objectType, null, options);
        }

        public SQLiteObjectTable GetObjectTable<T>(SQLiteBuildTableOptions options = null) => GetObjectTable(typeof(T), options);
        public virtual SQLiteObjectTable GetObjectTable(Type type, SQLiteBuildTableOptions options = null)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var key = type.FullName;
            if (options?.CacheKey != null)
            {
                // a character invalid in type names
                key += "!" + options.CacheKey;
            }

            if (!_objectTables.TryGetValue(key, out var table))
            {
                table = BuildObjectTable(type, options);
                table = _objectTables.AddOrUpdate(key, table, (k, o) => o);
            }
            return table;
        }

        protected virtual SQLiteObjectTable BuildObjectTable(Type type, SQLiteBuildTableOptions options = null)
        {
            var builder = CreateObjectTableBuilder(type, options);
            if (builder == null)
                throw new InvalidOperationException();

            return builder.Build();
        }

        public override string ToString() => FilePath;

        protected virtual SQLiteObjectTableBuilder CreateObjectTableBuilder(Type type, SQLiteBuildTableOptions options = null) => new SQLiteObjectTableBuilder(this, type, options);
        protected virtual SQLiteStatement CreateStatement(string sql, Func<SQLiteError, SQLiteOnErrorAction> prepareErrorHandler) => new SQLiteStatement(this, sql, prepareErrorHandler);
        protected virtual SQLiteRow CreateRow(int index, string[] names, object[] values) => new SQLiteRow(index, names, values);
        protected virtual SQLiteBlob CreateBlob(IntPtr handle, string tableName, string columnName, long rowId, SQLiteBlobOpenMode mode) => new SQLiteBlob(this, handle, tableName, columnName, rowId, mode);
        public virtual SQLiteLoadOptions CreateLoadOptions() => new SQLiteLoadOptions(this);
        public virtual SQLiteLoadForeignKeyOptions CreateLoadForeignKeyOptions() => new SQLiteLoadForeignKeyOptions(this);
        public virtual SQLiteSaveOptions CreateSaveOptions() => new SQLiteSaveOptions(this);
        public virtual SQLiteBindOptions CreateBindOptions() => new SQLiteBindOptions(this);
        public virtual SQLiteDeleteOptions CreateDeleteOptions() => new SQLiteDeleteOptions(this);
        public virtual SQLiteBuildTableOptions CreateBuildTableOptions() => new SQLiteBuildTableOptions(this);
        public virtual SQLiteBindContext CreateBindContext() => new SQLiteBindContext(this);

        public virtual int GetBlobSize(string tableName, string columnName, long rowId)
        {
            var sql = "SELECT length(" + SQLiteStatement.EscapeName(columnName) + ") FROM " + SQLiteStatement.EscapeName(tableName) + " WHERE rowid=" + rowId;
            return ExecuteScalar(sql, -1);
        }

        public virtual void ResizeBlob(string tableName, string columnName, long rowId, int size)
        {
            if (tableName == null)
                throw new ArgumentNullException(nameof(tableName));

            if (columnName == null)
                throw new ArgumentNullException(nameof(columnName));

            var sql = "UPDATE " + SQLiteStatement.EscapeName(tableName) + " SET " + SQLiteStatement.EscapeName(columnName) + "=? WHERE rowid=" + rowId;
            ExecuteNonQuery(sql, new SQLiteZeroBlob { Size = size });
        }

        public SQLiteBlob OpenBlob(string tableName, string columnName, long rowId) => OpenBlob(tableName, columnName, rowId, SQLiteBlobOpenMode.ReadOnly);
        public virtual SQLiteBlob OpenBlob(string tableName, string columnName, long rowId, SQLiteBlobOpenMode mode)
        {
            if (tableName == null)
                throw new ArgumentNullException(nameof(tableName));

            if (columnName == null)
                throw new ArgumentNullException(nameof(columnName));

            CheckError(_sqlite3_blob_open(CheckDisposed(), "main", tableName, columnName, rowId, (int)mode, out var handle));
            var blob = CreateBlob(handle, tableName, columnName, rowId, mode);
            if (blob == null)
                throw new InvalidOperationException();

            return blob;
        }

        public SQLiteStatement PrepareStatement(string sql, params object[] args) => PrepareStatement(sql, null, args);
        public virtual SQLiteStatement PrepareStatement(string sql, Func<SQLiteError, SQLiteOnErrorAction> errorHandler, params object[] args)
        {
            SQLiteStatement statement;
            if (errorHandler == null)
            {
                statement = GetOrCreateStatement(sql);
            }
            else
            {
                statement = CreateStatement(sql, errorHandler);
            }
            if (statement == null)
                throw new InvalidOperationException();

            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    statement.BindParameter(i + 1, args[i]);
                }
            }
            return statement;
        }

        protected virtual SQLiteStatement GetOrCreateStatement(string sql)
        {
            if (sql == null)
                throw new ArgumentNullException(nameof(sql));

            if (!EnableStatementsCache)
                return CreateStatement(sql, null);

            if (!_statementPools.TryGetValue(sql, out var pool))
            {
                pool = new StatementPool(sql, (s) => CreateStatement(s, null));
                pool = _statementPools.AddOrUpdate(sql, pool, (k, o) => o);
            }
            return pool.Get();
        }

        private sealed class StatementPool
        {
            internal ConcurrentBag<StatementPoolEntry> _statements = new ConcurrentBag<StatementPoolEntry>();

            public StatementPool(string sql, Func<string, SQLiteStatement> createFunc)
            {
                Sql = sql;
                CreateFunc = createFunc;
            }

            public string Sql { get; }
            public Func<string, SQLiteStatement> CreateFunc { get; }
            public int TotalUsage => _statements.Sum(s => s.Usage);

            public override string ToString() => Sql;

            // only ClearStatementsCache calls this once it got a hold on us
            // so we don't need locks or something here
            public void Clear()
            {
                while (!_statements.IsEmpty)
                {
                    StatementPoolEntry entry = null;
                    bool taken;
                    try
                    {
                        // for some reason, this can throw in rare conditions
                        taken = _statements.TryTake(out entry);
                    }
                    catch
                    {
                        taken = false;
                    }

                    if (taken && entry != null)
                    {
                        // if the statement was still in use, we can't dispose it
                        // so we just mark it so the user will really dispose it when he'll call Dispose()
                        if (Interlocked.CompareExchange(ref entry.Statement._locked, 1, 0) != 0)
                        {
                            entry.Statement._realDispose = true;
                        }
                        else
                        {
                            entry.Statement.RealDispose();
                        }
                    }
                }
            }

            public SQLiteStatement Get()
            {
                var entry = _statements.FirstOrDefault(s => s.Statement._locked == 0);
                if (entry != null && Interlocked.CompareExchange(ref entry.Statement._locked, 1, 0) != 0)
                {
                    // between the moment we got one and the moment we tried to lock it,
                    // another thread got it. In this case, we'll just create a new one...
                    entry = null;
                }

                if (entry == null)
                {
                    entry = new StatementPoolEntry();
                    entry.CreationDate = DateTime.Now;
                    entry.Statement = CreateFunc(Sql);
                    entry.Statement._realDispose = false;
                    entry.Statement._locked = 1;
                    _statements.Add(entry);
                }

                entry.LastUsageDate = DateTime.Now;
                entry.Usage++;
                return entry.Statement;
            }
        }

        private sealed class StatementPoolEntry
        {
            public SQLiteStatement Statement;
            public DateTime CreationDate;
            public DateTime LastUsageDate;
            public int Usage;

            public override string ToString() => Usage + " => " + Statement;
        }

        public virtual void ClearStatementsCache()
        {
            foreach (var key in _statementPools.Keys.ToArray())
            {
                if (_statementPools.TryRemove(key, out var pool))
                {
                    pool.Clear();
                }
            }
        }

        // for debugging purposes. returned object spec is not documented and may vary
        // it's recommended to use TableString utility to dump this, for example db.GetStatementsCacheEntries().ToTableString(Console.Out);
        public object[] GetStatementsCacheEntries()
        {
            var list = new List<object>();
            var pools = _statementPools.ToArray();
            foreach (var pool in pools)
            {
                var entries = pool.Value._statements.ToArray();
                foreach (var entry in entries)
                {
                    var o = new
                    {
                        pool.Value.Sql,
                        entry.CreationDate,
                        Duration = entry.LastUsageDate - entry.CreationDate,
                        entry.Usage,
                    };
                    list.Add(o);
                }
            }
            return list.ToArray();
        }

        public T ExecuteScalar<T>(string sql, params object[] args) => ExecuteScalar(sql, default(T), null, args);
        public T ExecuteScalar<T>(string sql, Func<SQLiteError, SQLiteOnErrorAction> errorHandler, params object[] args) => ExecuteScalar(sql, default(T), errorHandler, args);
        public T ExecuteScalar<T>(string sql, T defaultValue, params object[] args) => ExecuteScalar(sql, defaultValue, null, args);
        public virtual T ExecuteScalar<T>(string sql, T defaultValue, Func<SQLiteError, SQLiteOnErrorAction> errorHandler, params object[] args)
        {
            using (var statement = PrepareStatement(sql, errorHandler, args))
            {
                statement.StepOne(errorHandler);
                return statement.GetColumnValue(0, defaultValue);
            }
        }

        public object ExecuteScalar(string sql, params object[] args) => ExecuteScalar(sql, null, args);
        public virtual object ExecuteScalar(string sql, Func<SQLiteError, SQLiteOnErrorAction> errorHandler, params object[] args)
        {
            using (var statement = PrepareStatement(sql, errorHandler, args))
            {
                statement.StepOne(errorHandler);
                return statement.GetColumnValue(0);
            }
        }

        public int ExecuteNonQuery(string sql, params object[] args) => ExecuteNonQuery(sql, null, args);
        public virtual int ExecuteNonQuery(string sql, Func<SQLiteError, SQLiteOnErrorAction> errorHandler, params object[] args)
        {
            using (var statement = PrepareStatement(sql, errorHandler, args))
            {
                statement.StepOne(errorHandler);
                return ChangesCount;
            }
        }

        public IEnumerable<object[]> LoadObjects(string sql, params object[] args) => LoadObjects(sql, null, args);
        public virtual IEnumerable<object[]> LoadObjects(string sql, Func<SQLiteError, SQLiteOnErrorAction> errorHandler, params object[] args)
        {
            using (var statement = PrepareStatement(sql, errorHandler, args))
            {
                var index = 0;
                do
                {
                    var code = _sqlite3_step(statement.Handle);
                    if (code == SQLiteErrorCode.SQLITE_DONE)
                    {
                        index++;
                        Log(TraceLevel.Verbose, "Step done at index " + index);
                        break;
                    }

                    if (code == SQLiteErrorCode.SQLITE_ROW)
                    {
                        yield return statement.BuildRow().ToArray();
                        index++;
                        continue;
                    }

                    if (errorHandler != null)
                    {
                        var error = new SQLiteError(statement, index, code);
                        var action = errorHandler(error);
                        index = error.Index;
                        code = error.Code;
                        if (action == SQLiteOnErrorAction.Break)
                            break;

                        if (action == SQLiteOnErrorAction.Continue)
                        {
                            index++;
                            continue;
                        }

                        // else throw
                    }

                    CheckError(code, sql: sql);
                }
                while (true);
            }
        }

        public IEnumerable<SQLiteRow> LoadRows(string sql, params object[] args) => LoadRows(sql, null, args);
        public virtual IEnumerable<SQLiteRow> LoadRows(string sql, Func<SQLiteError, SQLiteOnErrorAction> errorHandler, params object[] args)
        {
            using (var statement = PrepareStatement(sql, errorHandler, args))
            {
                var index = 0;
                do
                {
                    var code = _sqlite3_step(statement.Handle);
                    if (code == SQLiteErrorCode.SQLITE_DONE)
                    {
                        index++;
                        Log(TraceLevel.Verbose, "Step done at index " + index);
                        break;
                    }

                    if (code == SQLiteErrorCode.SQLITE_ROW)
                    {
                        var values = statement.BuildRow().ToArray();
                        var row = CreateRow(index, statement.ColumnsNames, values);
                        if (row == null)
                            throw new InvalidOperationException();

                        yield return row;
                        index++;
                        continue;
                    }

                    if (errorHandler != null)
                    {
                        var error = new SQLiteError(statement, index, code);
                        var action = errorHandler(error);
                        index = error.Index;
                        code = error.Code;
                        if (action == SQLiteOnErrorAction.Break)
                            break;

                        if (action == SQLiteOnErrorAction.Continue)
                        {
                            index++;
                            continue;
                        }

                        // else throw
                    }

                    CheckError(code, sql: sql);
                }
                while (true);
            }
        }

        public T ChangeType<T>(object input) => ChangeType<T>(input, default);
        public T ChangeType<T>(object input, T defaultValue)
        {
            if (TryChangeType(input, out T value))
                return value;

            return defaultValue;
        }

        public object ChangeType(object input, Type conversionType)
        {
            if (conversionType == null)
                throw new ArgumentNullException(nameof(conversionType));

            if (TryChangeType(input, conversionType, out object value))
                return value;

            if (conversionType.IsValueType)
                return Activator.CreateInstance(conversionType);

            return null;
        }

        public object ChangeType(object input, Type conversionType, object defaultValue)
        {
            if (conversionType == null)
                throw new ArgumentNullException(nameof(conversionType));

            if (TryChangeType(input, conversionType, out object value))
                return value;

            if (TryChangeType(defaultValue, conversionType, out value))
                return value;

            if (conversionType.IsValueType)
                return Activator.CreateInstance(conversionType);

            return null;
        }

        // note: we always use invariant culture when writing an reading by ourselves to the database
        public virtual bool TryChangeType(object input, Type conversionType, out object value)
        {
            if (conversionType == null)
                throw new ArgumentNullException(nameof(conversionType));

            if (input != null && input.GetType() == conversionType)
            {
                value = input;
                return true;
            }

            if (typeof(ISQLiteObject).IsAssignableFrom(conversionType))
            {
                if (input == null)
                {
                    value = null;
                    return false;
                }

                var instance = LoadByPrimaryKey(conversionType, input);
                value = instance;
                return instance != null;
            }
            return Conversions.TryChangeType(input, conversionType, CultureInfo.InvariantCulture, out value);
        }

        public virtual bool TryChangeType<T>(object input, out T value)
        {
            if (!TryChangeType(input, typeof(T), out object obj))
            {
                value = default;
                return false;
            }

            value = (T)obj;
            return true;
        }

        public virtual void EnsureQuerySupportFunctions()
        {
            lock (new object())
            {
                if (_querySupportFunctionsAdded)
                    return;

                _querySupportFunctionsAdded = true;

                // https://sqlite.org/lang_corefunc.html#instr is only 2 args, we add one to add string comparison support
                SetScalarFunction("instr", 3, true, (c) =>
                {
                    var x = c.Values[0].StringValue;
                    var y = c.Values[1].StringValue;
                    if (x != null && y != null)
                    {
                        var sc = (StringComparison)c.Values[2].Int32Value;
                        c.SetResult(x.IndexOf(y, sc) + 1);
                    }
                });
            }
        }

        public void CreateIndex(string name, string tableName, IEnumerable<SQLiteIndexedColumn> columns) => CreateIndex(null, name, false, tableName, columns, null);
        public void CreateIndex(string name, bool unique, string tableName, IEnumerable<SQLiteIndexedColumn> columns) => CreateIndex(null, name, unique, tableName, columns, null);
        public virtual void CreateIndex(string schemaName, string name, bool unique, string tableName, IEnumerable<SQLiteIndexedColumn> columns, string whereExpression)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            if (tableName == null)
                throw new ArgumentNullException(nameof(tableName));

            if (columns == null)
                throw new ArgumentNullException(nameof(columns));

            if (!columns.Any())
                throw new ArgumentException(null, nameof(columns));

            var sql = "CREATE " + (unique ? "UNIQUE " : null) + "INDEX IF NOT EXISTS ";
            if (!string.IsNullOrWhiteSpace(schemaName))
            {
                sql += schemaName + ".";
            }

            sql += name + " ON " + SQLiteStatement.EscapeName(tableName) + " (";
            sql += string.Join(",", columns.Select(c => c.GetCreateSql()));
            sql += ")";

            if (!string.IsNullOrWhiteSpace(whereExpression))
            {
                sql += " WHERE " + whereExpression;
            }
            ExecuteNonQuery(sql);
        }

        public void DeleteIndex(string name) => DeleteIndex(null, name);
        public virtual void DeleteIndex(string schemaName, string name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            var sql = "DROP INDEX IF EXISTS ";
            if (!string.IsNullOrWhiteSpace(schemaName))
            {
                sql += schemaName + ".";
            }

            sql += name;
            ExecuteNonQuery(sql);
        }

        protected internal IntPtr CheckDisposed(bool throwOnError = true)
        {
            var handle = _handle;
            if (handle == IntPtr.Zero && throwOnError)
                throw new ObjectDisposedException(nameof(Handle));

            return handle;
        }

        internal static SQLiteException StaticCheckError(SQLiteErrorCode code, bool throwOnError)
        {
            if (code == SQLiteErrorCode.SQLITE_OK)
                return null;

            var ex = new SQLiteException(code);
            if (throwOnError)
                throw ex;

            return ex;
        }

        protected internal SQLiteException CheckError(SQLiteErrorCode code, [CallerMemberName] string methodName = null) => CheckError(code, null, true, methodName);
        protected internal SQLiteException CheckError(SQLiteErrorCode code, bool throwOnError, [CallerMemberName] string methodName = null) => CheckError(code, null, throwOnError, methodName);
        protected internal SQLiteException CheckError(SQLiteErrorCode code, string sql, [CallerMemberName] string methodName = null) => CheckError(code, sql, true, methodName);
        protected internal SQLiteException CheckError(SQLiteErrorCode code, string sql, bool throwOnError, [CallerMemberName] string methodName = null)
        {
            if (code == SQLiteErrorCode.SQLITE_OK)
                return null;

            var msg = GetErrorMessage(Handle); // don't check disposed here. maybe too late
            if (sql != null)
            {
                if (msg == null || !msg.EndsWith(".", StringComparison.Ordinal))
                {
                    msg += ".";
                }
                msg += " SQL statement was: `" + sql + "`";
            }

            var ex = msg != null ? new SQLiteException(code, msg) : new SQLiteException(code);
            Log(TraceLevel.Error, ex.Message, methodName);
            if (throwOnError)
                throw ex;

            return ex;
        }

        public int SetLimit(SQLiteLimit id, int newValue) => SetLimit((int)id, newValue);
        public virtual int SetLimit(int id, int newValue) => _sqlite3_limit(CheckDisposed(), id, newValue);
        public int GetLimit(SQLiteLimit id) => GetLimit((int)id);
        public virtual int GetLimit(int id) => _sqlite3_limit(CheckDisposed(), id, -1);

        public static string GetErrorMessage(IntPtr db)
        {
            if (db == IntPtr.Zero)
                return null;

            HookNativeProcs();
            var ptr = _sqlite3_errmsg16(db);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr);
        }

        // with this code, we support AnyCpu targets
        private static IEnumerable<string> PossibleNativePaths
        {
            get
            {
                var bd = AppDomain.CurrentDomain.BaseDirectory;
                var rsp = AppDomain.CurrentDomain.RelativeSearchPath;
                var bitness = IntPtr.Size == 8 ? "64" : "86";
                var searchRsp = rsp != null && !bd.EqualsIgnoreCase(rsp);

                // look for an env variable
                var env = GetEnvironmentVariable("SQLNADO_SQLITE_X" + bitness + "_DLL");
                if (env != null)
                {
                    // full path?
                    if (Path.IsPathRooted(env))
                    {
                        yield return env;
                    }
                    else
                    {
                        // relative path?
                        yield return Path.Combine(bd, env);
                        if (searchRsp)
                            yield return Path.Combine(rsp, env);
                    }
                }

                // look in appdomain path
                var name = "sqlite3.x" + bitness + ".dll";
                yield return Path.Combine(bd, name);
                if (searchRsp)
                    yield return Path.Combine(rsp, name);

                // look in windows/azure
                if (UseWindowsRuntime)
                    yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "winsqlite3.dll");

                name = "sqlite.dll";
                yield return Path.Combine(bd, name); // last resort, hoping the bitness's right, we do not recommend it
                if (searchRsp)
                    yield return Path.Combine(rsp, name);
            }
        }

        private static string GetEnvironmentVariable(string name)
        {
            try
            {
                var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process).Nullify();
                if (value != null)
                    return value;

                value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User).Nullify();
                if (value != null)
                    return value;

                return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine).Nullify();
            }
            catch
            {
                // probably an access denied, continue
                return null;
            }
        }

        private static void HookNativeProcs()
        {
            if (_module != IntPtr.Zero)
                return;

            var path = PossibleNativePaths.FirstOrDefault(p => File.Exists(p));
            if (path == null)
                throw new SqlNadoException("0002: Cannot determine native sqlite dll path. Process is running " + (IntPtr.Size == 8 ? "64" : "32") + "-bit.");

            NativeDllPath = path;
            IsUsingWindowsRuntime = Path.GetFileName(path).EqualsIgnoreCase("winsqlite3.dll");

#if !WINSQLITE
            if (IsUsingWindowsRuntime && IntPtr.Size == 4)
                throw new SqlNadoException("0029: SQLNado compilation is invalid. The process is running as 32-bit, using the Windows/Azure 'winsqlite3.dll' but without the WINSQLITE define. Contact the developer.");
#endif

            _module = LoadLibrary(path);
            if (_module == IntPtr.Zero)
                throw new SqlNadoException("0003: Cannot load native sqlite dll from path '" + path + "'. Process is running " + (IntPtr.Size == 8 ? "64" : "32") + "-bit.", new Win32Exception(Marshal.GetLastWin32Error()));

            _sqlite3_open_v2 = LoadProc<sqlite3_open_v2>();
            _sqlite3_close = LoadProc<sqlite3_close>();
            _sqlite3_errmsg16 = LoadProc<sqlite3_errmsg16>();
            _sqlite3_limit = LoadProc<sqlite3_limit>();
            _sqlite3_finalize = LoadProc<sqlite3_finalize>();
            _sqlite3_column_count = LoadProc<sqlite3_column_count>();
            _sqlite3_bind_parameter_count = LoadProc<sqlite3_bind_parameter_count>();
            _sqlite3_bind_parameter_index = LoadProc<sqlite3_bind_parameter_index>();
            _sqlite3_clear_bindings = LoadProc<sqlite3_clear_bindings>();
            _sqlite3_step = LoadProc<sqlite3_step>();
            _sqlite3_reset = LoadProc<sqlite3_reset>();
            _sqlite3_column_type = LoadProc<sqlite3_column_type>();
            _sqlite3_column_name16 = LoadProc<sqlite3_column_name16>();
            _sqlite3_column_blob = LoadProc<sqlite3_column_blob>();
            _sqlite3_column_bytes = LoadProc<sqlite3_column_bytes>();
            _sqlite3_column_double = LoadProc<sqlite3_column_double>();
            _sqlite3_column_int = LoadProc<sqlite3_column_int>();
            _sqlite3_column_int64 = LoadProc<sqlite3_column_int64>();
            _sqlite3_column_text16 = LoadProc<sqlite3_column_text16>();
            _sqlite3_prepare16_v2 = LoadProc<sqlite3_prepare16_v2>();
            _sqlite3_total_changes = LoadProc<sqlite3_total_changes>();
            _sqlite3_changes = LoadProc<sqlite3_changes>();
            _sqlite3_last_insert_rowid = LoadProc<sqlite3_last_insert_rowid>();
            _sqlite3_bind_text16 = LoadProc<sqlite3_bind_text16>();
            _sqlite3_bind_null = LoadProc<sqlite3_bind_null>();
            _sqlite3_bind_blob = LoadProc<sqlite3_bind_blob>();
            _sqlite3_bind_zeroblob = LoadProc<sqlite3_bind_zeroblob>();
            _sqlite3_bind_int = LoadProc<sqlite3_bind_int>();
            _sqlite3_bind_int64 = LoadProc<sqlite3_bind_int64>();
            _sqlite3_bind_double = LoadProc<sqlite3_bind_double>();
            _sqlite3_threadsafe = LoadProc<sqlite3_threadsafe>();
            _sqlite3_db_config_0 = LoadProc<sqlite3_db_config_0>("sqlite3_db_config");
            _sqlite3_db_config_1 = LoadProc<sqlite3_db_config_1>("sqlite3_db_config");
            _sqlite3_db_config_2 = LoadProc<sqlite3_db_config_2>("sqlite3_db_config");
            _sqlite3_enable_load_extension = LoadProc<sqlite3_enable_load_extension>();
            _sqlite3_load_extension = LoadProc<sqlite3_load_extension>();
            _sqlite3_config_0 = LoadProc<sqlite3_config_0>("sqlite3_config");
            _sqlite3_config_1 = LoadProc<sqlite3_config_1>("sqlite3_config");
            _sqlite3_config_2 = LoadProc<sqlite3_config_2>("sqlite3_config");
            _sqlite3_config_3 = LoadProc<sqlite3_config_3>("sqlite3_config");
            _sqlite3_config_4 = LoadProc<sqlite3_config_4>("sqlite3_config");
            _sqlite3_enable_shared_cache = LoadProc<sqlite3_enable_shared_cache>();
            _sqlite3_blob_bytes = LoadProc<sqlite3_blob_bytes>();
            _sqlite3_blob_close = LoadProc<sqlite3_blob_close>();
            _sqlite3_blob_open = LoadProc<sqlite3_blob_open>();
            _sqlite3_blob_read = LoadProc<sqlite3_blob_read>();
            _sqlite3_blob_reopen = LoadProc<sqlite3_blob_reopen>();
            _sqlite3_blob_write = LoadProc<sqlite3_blob_write>();
            _sqlite3_collation_needed16 = LoadProc<sqlite3_collation_needed16>();
            _sqlite3_create_collation16 = LoadProc<sqlite3_create_collation16>();
            _sqlite3_db_cacheflush = LoadProc<sqlite3_db_cacheflush>();
            _sqlite3_table_column_metadata = LoadProc<sqlite3_table_column_metadata>();
            _sqlite3_create_function16 = LoadProc<sqlite3_create_function16>();
            _sqlite3_value_blob = LoadProc<sqlite3_value_blob>();
            _sqlite3_value_double = LoadProc<sqlite3_value_double>();
            _sqlite3_value_int = LoadProc<sqlite3_value_int>();
            _sqlite3_value_int64 = LoadProc<sqlite3_value_int64>();
            _sqlite3_value_text16 = LoadProc<sqlite3_value_text16>();
            _sqlite3_value_bytes = LoadProc<sqlite3_value_bytes>();
            _sqlite3_value_bytes16 = LoadProc<sqlite3_value_bytes16>();
            _sqlite3_value_type = LoadProc<sqlite3_value_type>();
            _sqlite3_result_blob = LoadProc<sqlite3_result_blob>();
            _sqlite3_result_double = LoadProc<sqlite3_result_double>();
            _sqlite3_result_error16 = LoadProc<sqlite3_result_error16>();
            _sqlite3_result_error_code = LoadProc<sqlite3_result_error_code>();
            _sqlite3_result_int = LoadProc<sqlite3_result_int>();
            _sqlite3_result_int64 = LoadProc<sqlite3_result_int64>();
            _sqlite3_result_null = LoadProc<sqlite3_result_null>();
            _sqlite3_result_text16 = LoadProc<sqlite3_result_text16>();
            _sqlite3_result_zeroblob = LoadProc<sqlite3_result_zeroblob>();
        }

        private static T LoadProc<T>() => LoadProc<T>(null);
        private static T LoadProc<T>(string name)
        {
            if (name == null)
            {
                name = typeof(T).Name;
            }

            var address = GetProcAddress(_module, name);
            if (address == IntPtr.Zero)
                throw new SqlNadoException("0004: Cannot load library function '" + name + "' from '" + NativeDllPath + "'. Please make sure sqlite is the latest one.", new Win32Exception(Marshal.GetLastWin32Error()));

            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
#pragma warning disable CA2101 // Specify marshaling for P/Invoke string arguments
        private static extern IntPtr GetProcAddress(IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string lpProcName);
#pragma warning restore CA2101 // Specify marshaling for P/Invoke string arguments

        [DllImport("kernel32")]
        internal static extern long GetTickCount64();

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate IntPtr sqlite3_errmsg16(IntPtr db);
        private static sqlite3_errmsg16 _sqlite3_errmsg16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate int sqlite3_limit(IntPtr db, int id, int newVal);
        private static sqlite3_limit _sqlite3_limit;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_open_v2([MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Utf8Marshaler))] string filename, out IntPtr ppDb, SQLiteOpenOptions flags, IntPtr zvfs);
        private static sqlite3_open_v2 _sqlite3_open_v2;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_close(IntPtr db);
        private static sqlite3_close _sqlite3_close;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_finalize(IntPtr statement);
        internal static sqlite3_finalize _sqlite3_finalize;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate int sqlite3_column_count(IntPtr statement);
        internal static sqlite3_column_count _sqlite3_column_count;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate int sqlite3_bind_parameter_count(IntPtr statement);
        internal static sqlite3_bind_parameter_count _sqlite3_bind_parameter_count;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate int sqlite3_bind_parameter_index(IntPtr statement, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Utf8Marshaler))] string name);
        internal static sqlite3_bind_parameter_index _sqlite3_bind_parameter_index;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_clear_bindings(IntPtr statement);
        internal static sqlite3_clear_bindings _sqlite3_clear_bindings;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_step(IntPtr statement);
        internal static sqlite3_step _sqlite3_step;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_reset(IntPtr statement);
        internal static sqlite3_reset _sqlite3_reset;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteColumnType sqlite3_column_type(IntPtr statement, int index);
        internal static sqlite3_column_type _sqlite3_column_type;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate IntPtr sqlite3_column_name16(IntPtr statement, int index);
        internal static sqlite3_column_name16 _sqlite3_column_name16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate IntPtr sqlite3_column_blob(IntPtr statement, int index);
        internal static sqlite3_column_blob _sqlite3_column_blob;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate int sqlite3_column_bytes(IntPtr statement, int index);
        internal static sqlite3_column_bytes _sqlite3_column_bytes;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate double sqlite3_column_double(IntPtr statement, int index);
        internal static sqlite3_column_double _sqlite3_column_double;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate int sqlite3_column_int(IntPtr statement, int index);
        internal static sqlite3_column_int _sqlite3_column_int;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate long sqlite3_column_int64(IntPtr statement, int index);
        internal static sqlite3_column_int64 _sqlite3_column_int64;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate IntPtr sqlite3_column_text16(IntPtr statement, int index);
        internal static sqlite3_column_text16 _sqlite3_column_text16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string sql, int numBytes, out IntPtr statement, IntPtr tail);
        internal static sqlite3_prepare16_v2 _sqlite3_prepare16_v2;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate int sqlite3_total_changes(IntPtr db);
        private static sqlite3_total_changes _sqlite3_total_changes;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate int sqlite3_changes(IntPtr db);
        private static sqlite3_changes _sqlite3_changes;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate long sqlite3_last_insert_rowid(IntPtr db);
        private static sqlite3_last_insert_rowid _sqlite3_last_insert_rowid;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_bind_text16(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPWStr)] string text, int count, IntPtr xDel);
        internal static sqlite3_bind_text16 _sqlite3_bind_text16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_bind_null(IntPtr statement, int index);
        internal static sqlite3_bind_null _sqlite3_bind_null;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_bind_blob(IntPtr statement, int index, byte[] data, int size, IntPtr xDel);
        internal static sqlite3_bind_blob _sqlite3_bind_blob;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_bind_zeroblob(IntPtr statement, int index, int size);
        internal static sqlite3_bind_zeroblob _sqlite3_bind_zeroblob;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_bind_double(IntPtr statement, int index, double value);
        internal static sqlite3_bind_double _sqlite3_bind_double;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_bind_int64(IntPtr statement, int index, long value);
        internal static sqlite3_bind_int64 _sqlite3_bind_int64;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_bind_int(IntPtr statement, int index, int value);
        internal static sqlite3_bind_int _sqlite3_bind_int;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_blob_open(IntPtr db,
            [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Utf8Marshaler))] string database,
            [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Utf8Marshaler))] string table,
            [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Utf8Marshaler))] string column,
            long rowId, int flags, out IntPtr blob);
        internal static sqlite3_blob_open _sqlite3_blob_open;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate int sqlite3_blob_bytes(IntPtr blob);
        internal static sqlite3_blob_bytes _sqlite3_blob_bytes;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_blob_close(IntPtr blob);
        internal static sqlite3_blob_close _sqlite3_blob_close;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_blob_reopen(IntPtr blob, long rowId);
        internal static sqlite3_blob_reopen _sqlite3_blob_reopen;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_blob_read(IntPtr blob, byte[] buffer, int count, int offset);
        internal static sqlite3_blob_read _sqlite3_blob_read;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_blob_write(IntPtr blob, byte[] buffer, int count, int offset);
        internal static sqlite3_blob_write _sqlite3_blob_write;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_db_cacheflush(IntPtr db);
        private static sqlite3_db_cacheflush _sqlite3_db_cacheflush;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate int xCompare(IntPtr arg,
            int lenA, IntPtr strA,
            int lenB, IntPtr strB);

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_create_collation16(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string name, SQLiteTextEncoding encoding, IntPtr arg, xCompare comparer);
        private static sqlite3_create_collation16 _sqlite3_create_collation16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate void collationNeeded(IntPtr arg, IntPtr db, SQLiteTextEncoding encoding, [MarshalAs(UnmanagedType.LPWStr)] string strB);

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_collation_needed16(IntPtr db, IntPtr arg, collationNeeded callback);
        private static sqlite3_collation_needed16 _sqlite3_collation_needed16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteErrorCode sqlite3_table_column_metadata(IntPtr db, string dbname, string tablename, string columnname, out IntPtr dataType, out IntPtr collation, out int notNull, out int pk, out int autoInc);
        internal static sqlite3_table_column_metadata _sqlite3_table_column_metadata;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate void xFunc(IntPtr context, int argsCount, [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] args);

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate void xFinal(IntPtr context);

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_create_function16(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string name,
            int argsCount, SQLiteTextEncoding encoding, IntPtr app, xFunc func, xFunc step, xFinal final);
        private static sqlite3_create_function16 _sqlite3_create_function16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate IntPtr sqlite3_value_blob(IntPtr value);
        internal static sqlite3_value_blob _sqlite3_value_blob;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate double sqlite3_value_double(IntPtr value);
        internal static sqlite3_value_double _sqlite3_value_double;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate int sqlite3_value_int(IntPtr value);
        internal static sqlite3_value_int _sqlite3_value_int;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate long sqlite3_value_int64(IntPtr value);
        internal static sqlite3_value_int64 _sqlite3_value_int64;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate IntPtr sqlite3_value_text16(IntPtr value);
        internal static sqlite3_value_text16 _sqlite3_value_text16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate int sqlite3_value_bytes16(IntPtr value);
        internal static sqlite3_value_bytes16 _sqlite3_value_bytes16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate int sqlite3_value_bytes(IntPtr value);
        internal static sqlite3_value_bytes _sqlite3_value_bytes;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate SQLiteColumnType sqlite3_value_type(IntPtr value);
        internal static sqlite3_value_type _sqlite3_value_type;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate void sqlite3_result_blob(IntPtr ctx, byte[] buffer, int size, IntPtr xDel);
        private static sqlite3_result_blob _sqlite3_result_blob;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate void sqlite3_result_double(IntPtr ctx, double value);
        internal static sqlite3_result_double _sqlite3_result_double;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate void sqlite3_result_error16(IntPtr ctx, [MarshalAs(UnmanagedType.LPWStr)] string value, int len);
        internal static sqlite3_result_error16 _sqlite3_result_error16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate void sqlite3_result_error_code(IntPtr ctx, SQLiteErrorCode value);
        internal static sqlite3_result_error_code _sqlite3_result_error_code;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate void sqlite3_result_int(IntPtr ctx, int value);
        internal static sqlite3_result_int _sqlite3_result_int;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate void sqlite3_result_int64(IntPtr ctx, long value);
        internal static sqlite3_result_int64 _sqlite3_result_int64;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate void sqlite3_result_null(IntPtr ctx);
        internal static sqlite3_result_null _sqlite3_result_null;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate void sqlite3_result_text16(IntPtr ctx, [MarshalAs(UnmanagedType.LPWStr)] string value, int len, IntPtr xDel);
        internal static sqlite3_result_text16 _sqlite3_result_text16;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        internal delegate void sqlite3_result_zeroblob(IntPtr ctx, int size);
        internal static sqlite3_result_zeroblob _sqlite3_result_zeroblob;

        [Flags]
        private enum SQLiteTextEncoding
        {
            SQLITE_UTF8 = 1,                /* IMP: R-37514-35566 */
            SQLITE_UTF16LE = 2,             /* IMP: R-03371-37637 */
            SQLITE_UTF16BE = 3,             /* IMP: R-51971-34154 */
            SQLITE_UTF16 = 4,               /* Use native byte order */
            SQLITE_ANY = 5,                 /* Deprecated */
            SQLITE_UTF16_ALIGNED = 8,       /* sqlite3_create_collation only */
            SQLITE_DETERMINISTIC = 0x800,    // function will always return the same result given the same inputs within a single SQL statement
        }

        // https://sqlite.org/c3ref/threadsafe.html
#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate int sqlite3_threadsafe();
        private static sqlite3_threadsafe _sqlite3_threadsafe;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_enable_shared_cache(int i);
        private static sqlite3_enable_shared_cache _sqlite3_enable_shared_cache;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_config_0(SQLiteConfiguration op);
        private static sqlite3_config_0 _sqlite3_config_0;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_config_1(SQLiteConfiguration op, long i);
        private static sqlite3_config_1 _sqlite3_config_1;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_config_2(SQLiteConfiguration op, int i);
        private static sqlite3_config_2 _sqlite3_config_2;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_config_3(SQLiteConfiguration op, long i1, long i2);
        private static sqlite3_config_3 _sqlite3_config_3;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_config_4(SQLiteConfiguration op, int i1, int i2);
        private static sqlite3_config_4 _sqlite3_config_4;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_db_config_0(IntPtr db, SQLiteDatabaseConfiguration op, int i, out int result);
        private static sqlite3_db_config_0 _sqlite3_db_config_0;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_db_config_1(IntPtr db, SQLiteDatabaseConfiguration op, IntPtr ptr, int i0, int i1);
        private static sqlite3_db_config_1 _sqlite3_db_config_1;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_db_config_2(IntPtr db, SQLiteDatabaseConfiguration op, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Utf8Marshaler))] string s);
        private static sqlite3_db_config_2 _sqlite3_db_config_2;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_enable_load_extension(IntPtr db, int onoff);
        private static sqlite3_enable_load_extension _sqlite3_enable_load_extension;

#if !WINSQLITE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
        private delegate SQLiteErrorCode sqlite3_load_extension(IntPtr db,
            [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Utf8Marshaler))] string zFile,
            [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Utf8Marshaler))] string zProc,
            out IntPtr pzErrMsg);
        private static sqlite3_load_extension _sqlite3_load_extension;

        internal class Utf8Marshaler : ICustomMarshaler
        {
            public static readonly Utf8Marshaler Instance = new Utf8Marshaler();

            // *must* exist for a custom marshaler
#pragma warning disable IDE0060 // Remove unused parameter
            public static ICustomMarshaler GetInstance(string cookie) => Instance;
#pragma warning restore IDE0060 // Remove unused parameter

            public void CleanUpManagedData(object ManagedObj)
            {
                // nothing to do
            }

            public void CleanUpNativeData(IntPtr pNativeData)
            {
                if (pNativeData != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pNativeData);
                }
            }

            public int GetNativeDataSize() => -1;

            public IntPtr MarshalManagedToNative(object ManagedObj)
            {
                if (ManagedObj == null)
                    return IntPtr.Zero;

                // add a terminating zero
                var bytes = Encoding.UTF8.GetBytes((string)ManagedObj + '\0');
                var ptr = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                return ptr;
            }

            public object MarshalNativeToManaged(IntPtr pNativeData)
            {
                if (pNativeData == IntPtr.Zero)
                    return null;

                // look for the terminating zero
                var i = 0;
                while (Marshal.ReadByte(pNativeData, i) != 0)
                {
                    i++;
                }

                var bytes = new byte[i];
                Marshal.Copy(pNativeData, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            _enableStatementsCache = false;
            if (disposing)
            {
                ClearStatementsCache();
            }

            // note we could have a small race condition if someone adds a tokenizer between ToArray and Clear
            // well, beyond the fact it should not happen a lot, we'll just loose a bit of memory
            var toks = _tokenizers.ToArray();
            _tokenizers.Clear();
            foreach (var tok in toks)
            {
                tok.Value.Dispose();
            }

            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                _sqlite3_collation_needed16(handle, IntPtr.Zero, null);
                _sqlite3_close(handle);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SQLiteDatabase() => Dispose(false);
    }
}
