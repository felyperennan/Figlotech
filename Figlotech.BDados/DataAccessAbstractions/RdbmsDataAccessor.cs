using Figlotech.BDados.Builders;
using Figlotech.BDados.DataAccessAbstractions.Attributes;
using Figlotech.BDados.Helpers;
using Figlotech.Core;
using Figlotech.Core.BusinessModel;
using Figlotech.Core.Helpers;
using Figlotech.Core.Interfaces;
using Figlotech.Data;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Figlotech.BDados.DataAccessAbstractions {

    public sealed class BDadosTransaction : IDisposable {
        public IDbConnection Connection { get; private set; }
        private IDbTransaction Transaction { get; set; }
        public Benchmarker Benchmarker { get; set; }
        internal RdbmsDataAccessor DataAccessor { get; set; }
        public ConnectionState ConnectionState => Connection.State;
        public Object ContextTransferObject { get; set; }

        internal bool usingExternalBenchmarker { get; set; }

        public bool IsUsingRdbmsTransaction => Transaction != null;

        public BDadosTransaction(RdbmsDataAccessor rda, IDbConnection connection) {
            DataAccessor = rda;
            Connection = connection;
        }

        ~BDadosTransaction() {
            Dispose();
        }

        public List<string[]> FrameHistory { get; private set; } = new List<string[]>(200);

        private List<IDataObject> ObjectsToNotify { get; set; } = new List<IDataObject>();
        public Action OnTransactionEnding { get; internal set; }

        public void NotifyChange(IDataObject[] ido) {
            if (Transaction == null) {
                DataAccessor.RaiseForChangeIn(ido);
            } else {
                lock (ObjectsToNotify)
                    ObjectsToNotify.AddRange(ido);
            }
        }

        public void Step() {
            if (Connection.State != ConnectionState.Open) {
                DataAccessor.OpenConnection(Connection);
            }
            if (!FiTechCoreExtensions.EnableDebug)
                return;
            try {
                StackTrace trace = new StackTrace(0, false);
                var frames = trace.GetFrames();
                int i = 0;
                while (frames[i].GetMethod().DeclaringType == typeof(RdbmsDataAccessor)) {
                    i++;
                }
                FrameHistory.Add(trace.GetFrames().Skip(i).Take(20).Select((frame) => {
                    var type = frame.GetMethod().DeclaringType;
                    return $"{(type?.Name ?? "")} -> " + frame.ToString();
                }).ToArray());
            } catch (Exception x) {
                if(Debugger.IsAttached) {
                    Debugger.Break();
                }
            }
        }

        public IDbCommand CreateCommand(IQueryBuilder query) {
            var retv = CreateCommand();
            query.ApplyToCommand(retv, this.DataAccessor.Plugin.ProcessParameterValue);
            return retv;
        }

        public IDbCommand CreateCommand(string query = null) {
            var retv = CreateCommand();
            retv.CommandText = query;
            return retv;
        }

        public IDbCommand CreateCommand() {
            Step();
            var retv = Connection?.CreateCommand();
            retv.Transaction = Transaction;
            return retv;
        }

        public void BeginTransaction(IsolationLevel ilev = IsolationLevel.ReadUncommitted) {
            Transaction = Connection?.BeginTransaction(ilev);
        }

        public void Commit() {
            Transaction?.Commit();
            lock (ObjectsToNotify) {
                DataAccessor.RaiseForChangeIn(ObjectsToNotify.ToArray());
                ObjectsToNotify.Clear();
            }
        }

        public void Rollback() {
            Transaction?.Rollback();
            lock (ObjectsToNotify)
                ObjectsToNotify.Clear();
        }

        public void EndTransaction() {
            var conn = Connection;
            if (OnTransactionEnding != null) {
                this.OnTransactionEnding.Invoke();
            }
            Transaction?.Dispose();
            conn?.Dispose();
        }

        public void Dispose() {
            if(Transaction?.Connection?.State == ConnectionState.Open) {
                try {
                    Transaction?.Dispose();
                } catch (Exception x) {
                    Fi.Tech.WriteLine($"Warning disposing BDadosTransaction: {x.Message}");
                }
                try {
                    Connection.Dispose();
                } catch (Exception x) {
                    Fi.Tech.WriteLine($"Warning disposing connection from BDadosTransaction: {x.Message}");
                }
            }
            RdbmsDataAccessor.ActiveConnections.RemoveAll(x => x.Connection == Connection);
        }
    }
    
    public partial class RdbmsDataAccessor : IRdbmsDataAccessor, IDisposable {

        public static List<(IDbConnection Connection, string StackData, int AccessorId)> ActiveConnections = new List<(IDbConnection Connection, string StackData, int AccessorId)>();

        public ILogger Logger { get; set; }

        public static int DefaultMaxOpenAttempts { get; set; } = 5;
        public static int DefaultOpenAttemptInterval { get; set; } = 100;

        public Benchmarker Benchmarker { get; set; }

        public Type[] _workingTypes = new Type[0];
        public Type[] WorkingTypes {
            get { return _workingTypes; }
            set {
                _workingTypes = value.Where(t => t.GetInterfaces().Contains(typeof(IDataObject))).ToArray();
            }
        }

        internal void RaiseForChangeIn(IDataObject[] ido) {
            if (!ido.Any()) {
                return;
            }
            OnDataObjectAltered?.Invoke(ido.First().GetType(), ido);
        }
        public static RdbmsDataAccessor Using<T>(IDictionary<String, object> Configuration) where T : IRdbmsPluginAdapter, new() {
            var Plugin = new T();
            Plugin.SetConfiguration(Configuration);
            return new RdbmsDataAccessor(
                Plugin
            );
        }

        #region **** Global Declaration ****
        internal IRdbmsPluginAdapter Plugin;

        private int _simmultaneoustransactions;


        private bool _accessSwitch = false;

        public String SchemaName { get { return Plugin.SchemaName; } }

        private static int counter = 0;
        private int myId = ++counter;
        private String _readLock = $"readLock{counter + 1}";

        public static String Version {
            get {
                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
        }

        #endregion **************************
        //
        #region **** General Functions ****

        public RdbmsDataAccessor(IRdbmsPluginAdapter extension) {
            Plugin = extension;
        }

        public void EnsureDatabaseExists() {
            lock (Plugin) {
                using (var conn = Plugin.GetNewSchemalessConnection()) {
                    OpenConnection(conn);
                    var query = Plugin.QueryGenerator.CreateDatabase(Plugin.SchemaName);
                    using (var command = conn.CreateCommand()) {
                        query.ApplyToCommand(command, Plugin.ProcessParameterValue);
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        public T ForceExist<T>(Func<T> Default, IQueryBuilder qb) where T : IDataObject, new() {
            if (CurrentTransaction != null) {
                return ForceExist<T>(CurrentTransaction, Default, qb);
            }
            return Access((transaction) => ForceExist<T>(transaction, Default, qb));
        }

        public string GetCreateTable(String table) {
            return ScalarQuery<String>(Qb.Fmt($"SHOW CREATE TABLE {table}"));
        }

        public bool showPerformanceLogs = false;

        public void Backup(Stream s) {
            throw new NotImplementedException("It's... Not implemented yet");
        }

        public T ForceExist<T>(Func<T> Default, Conditions<T> qb) where T : IDataObject, new() {
            var f = LoadAll<T>(Core.Interfaces.LoadAll.Where<T>(qb));
            if (f.Any()) {
                return f.First();
            } else {
                T quickSave = Default();
                SaveItem(quickSave);
                return quickSave;
            }
        }

        bool isOnline = true;

        public bool Test() {
            if (isOnline) {
                return true;
            }
            try {
                Execute("SELECT 1");
                return isOnline = true;
            } catch (Exception x) {
                throw new BDadosException("Error testing connection to the database", x);
            }
        }

        #endregion ***************************

        public T LoadFirstOrDefault<T>(LoadAllArgs<T> args = null) where T : IDataObject, new() {
            return LoadAll<T>(args).FirstOrDefault();
        }

        public int ThreadId => Thread.CurrentThread.ManagedThreadId;

        Dictionary<int, BDadosTransaction> _currentTransaction = new Dictionary<int, BDadosTransaction>();
        BDadosTransaction CurrentTransaction {
            get {
                if (_currentTransaction.ContainsKey(ThreadId)) {
                    if(_currentTransaction[ThreadId] != null && _currentTransaction[ThreadId]?.ConnectionState == ConnectionState.Closed) {
                        try {
                            _currentTransaction[ThreadId].Dispose();
                        } catch(Exception x) {
                            Fi.Tech.Throw(x);
                        }
                        return null;
                    } else {
                        return _currentTransaction[ThreadId];
                    }
                }
                return null;
            }
            set {
                _currentTransaction[ThreadId] = value;
            }
        }

        public void SetContext(object contextObject) {
            if (CurrentTransaction != null) {
                CurrentTransaction.ContextTransferObject = contextObject;
            }
        }

        public List<FieldAttribute> GetInfoSchemaColumns() {
            var dbName = this.SchemaName;
            var map = Plugin.InfoSchemaColumnsMap;

            List<FieldAttribute> retv = new List<FieldAttribute>();

            return UseTransaction((conn) => {
                using (var cmd = conn.CreateCommand(this.QueryGenerator.InformationSchemaQueryColumns(dbName))) {
                    using (var reader = cmd.ExecuteReader()) {
                        return Fi.Tech.ReaderToObjectListUsingMap<FieldAttribute>(reader, map);
                    }
                }
            }, x=> {
                throw x;
            });

        }

        public IRdbmsDataAccessor Fork() {
            return new RdbmsDataAccessor(Plugin);
        }

        public BDadosTransaction BeginTransaction(IsolationLevel ilev = IsolationLevel.ReadUncommitted, Benchmarker bmark = null) {
            lock (this) {
                if (CurrentTransaction == null) {
                    //if (FiTechCoreExtensions.EnableDebug) {
                    //    WriteLog(Environment.StackTrace);
                    //}
                    WriteLog("Opening Transaction");
                    var connection = Plugin.GetNewConnection();
                    try {
                        OpenConnection(connection);
                    } catch(Exception x) {
                        try {
                            connection?.Dispose();
                        } catch (Exception) {

                        }
                        throw x;
                    }
                    CurrentTransaction = new BDadosTransaction(this, connection);
                    CurrentTransaction?.BeginTransaction(ilev);
                    CurrentTransaction.Benchmarker = bmark ?? Benchmarker ?? new Benchmarker("Database Access");
                    CurrentTransaction.usingExternalBenchmarker = bmark != null;
                    WriteLog("Transaction Open");
                }
                return CurrentTransaction;
            }
        }

        public void EndTransaction() {
            //lock (this) {
            if (CurrentTransaction != null) {
                WriteLog("Ending Transaction");
                CurrentTransaction?.EndTransaction();
                if (!(CurrentTransaction?.usingExternalBenchmarker ?? true)) {
                    CurrentTransaction?.Benchmarker?.FinalMark();
                }
                CurrentTransaction = null;

                WriteLog("Transaction ended");
            }
            //}
        }

        public void Commit() {
            if (CurrentTransaction != null && CurrentTransaction.IsUsingRdbmsTransaction) {
                WriteLog("Committing Transaction");
                lock (CurrentTransaction) {
                    CurrentTransaction?.Commit();
                }

                WriteLog("Commit OK");
            }
        }

        public void Rollback() {
            if (CurrentTransaction != null && CurrentTransaction.IsUsingRdbmsTransaction) {
                WriteLog("Rolling back Transaction");
                //lock (this)
                CurrentTransaction?.Rollback();
                WriteLog("Rollback OK");
            }
        }

        private static String GetDatabaseType(FieldInfo field, FieldAttribute info = null) {
            if (info == null)
                foreach (var att in field.GetCustomAttributes())
                    if (att is FieldAttribute) {
                        info = (FieldAttribute)att; break;
                    }
            if (info == null)
                return "VARCHAR(100)";

            string tipoDados;
            if (Nullable.GetUnderlyingType(field.FieldType) != null)
                tipoDados = Nullable.GetUnderlyingType(field.FieldType).Name;
            else
                tipoDados = field.FieldType.Name;
            if (field.FieldType.IsEnum) {
                return "INT";
            }
            String type = "VARCHAR(20)";
            if (info.Type != null && info.Type.Length > 0) {
                type = info.Type;
            } else {
                switch (tipoDados.ToLower()) {
                    case "string":
                        type = $"VARCHAR({info.Size})";
                        break;
                    case "int":
                    case "int32":
                        type = $"INT";
                        break;
                    case "uint":
                    case "uint32":
                        type = $"INT UNSIGNED";
                        break;
                    case "short":
                    case "int16":
                        type = $"SMALLINT";
                        break;
                    case "ushort":
                    case "uint16":
                        type = $"SMALLINT UNSIGNED";
                        break;
                    case "long":
                    case "int64":
                        type = $"BIGINT";
                        break;
                    case "ulong":
                    case "uint64":
                        type = $"BIGINT UNSIGNED";
                        break;
                    case "bool":
                    case "boolean":
                        type = $"TINYINT(1)";
                        break;
                    case "float":
                    case "double":
                    case "single":
                        type = $"FLOAT(16,3)";
                        break;
                    case "datetime":
                        type = $"DATETIME";
                        break;
                }
            }
            return type;
        }

        private MemberInfo FindMember(Expression x) {
            if (x == null)
                return null;
            MemberInfo retv = null;

            if (x is LambdaExpression lex) {
                retv = FindMember(lex.Body);
            }

            if (x is UnaryExpression) {
                retv = FindMember((x as UnaryExpression).Operand);
            }
            if (x is MemberExpression) {
                retv = (x as MemberExpression).Member;
            }
            if (x is BinaryExpression bex) {
                retv = FindMember(bex.Left);
            }

            if (retv == null)
                throw new MissingMemberException($"Member not found: {x.ToString()}");

            return retv;
        }

        public MemberInfo GetOrderingMember<T>(Expression<Func<T, object>> fn) {
            var OrderingMember = FindMember(fn);
            return OrderingMember;
        }

        private static String GetColumnDefinition(FieldInfo field, FieldAttribute info = null) {
            if (info == null)
                info = field.GetCustomAttribute<FieldAttribute>();
            if (info == null)
                return "VARCHAR(128)";
            var nome = field.Name;
            String tipo = GetDatabaseType(field, info);
            if (info.Type != null && info.Type.Length > 0)
                tipo = info.Type;
            var options = "";
            if (info.Options != null && info.Options.Length > 0) {
                options = info.Options;
            } else {
                if (!info.AllowNull) {
                    options += " NOT NULL";
                } else if (Nullable.GetUnderlyingType(field.GetType()) == null && field.FieldType.IsValueType && !info.AllowNull) {
                    options += " NOT NULL";
                }
                //if (info.Unique)
                //    options += " UNIQUE";
                if ((info.AllowNull && info.DefaultValue == null) || info.DefaultValue != null)
                    options += $" DEFAULT {CheapSanitize(info.DefaultValue)}";
                foreach (var att in field.GetCustomAttributes())
                    if (att is PrimaryKeyAttribute)
                        options += " AUTO_INCREMENT PRIMARY KEY";
            }

            return $"{nome} {tipo} {options}";
        }

        internal static String CheapSanitize(Object value) {
            String valOutput;
            if (value == null)
                return "NULL";
            if (value.GetType().IsEnum) {
                return $"{(int)Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()))}";
            }
            // We know for sure that value is not null at this point
            // But it may still be nullable.
            var checkingType = value.GetType();
            switch (value.GetType().Name.ToLower()) {
                case "string":
                    if (value.ToString() == "CURRENT_TIMESTAMP")
                        return "CURRENT_TIMESTAMP";
                    valOutput = ((String)value);
                    valOutput = valOutput.Replace("\\", "\\\\");
                    valOutput = valOutput.Replace("\'", "\\\'");
                    valOutput = valOutput.Replace("\"", "\\\"");
                    return $"'{valOutput}'";
                case "float":
                case "double":
                case "decimal":
                    valOutput = Convert.ToString(value).Replace(",", ".");
                    return $"{valOutput}";
                case "short":
                case "int":
                case "long":
                case "int16":
                case "int32":
                case "int64":
                    return Convert.ToString(value);
                case "datetime":
                    return $"'{((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss")}'";
                default:
                    return $"'{Convert.ToString(value)}'";
            }

        }

        public bool SaveItem(IDataObject input) {
            if (CurrentTransaction != null) {
                return SaveItem(CurrentTransaction, input);
            }
            return Access((transaction) => {
                return SaveItem(transaction, input);
            }, (x) => {
                //this.WriteLog(x.Message);
                //this.WriteLog(x.StackTrace);
                throw new BDadosException("Error saving item", x);
            });
        }

        public List<T> Query<T>(IQueryBuilder query) where T : new() {
            if (CurrentTransaction != null) {
                return Query<T>(CurrentTransaction, query);
            }
            return Access((transaction) => Query<T>(transaction, query));
        }

        public static String GetIdColumn<T>() where T : IDataObject, new() { return GetIdColumn(typeof(T)); }
        public static String GetIdColumn(Type type) {
            var fields = new List<FieldInfo>();
            do {
                fields.AddRange(type.GetFields());
                type = type.BaseType;
            } while (type != null);

            var retv = fields
                .Where((f) => f.GetCustomAttribute<PrimaryKeyAttribute>() != null)
                .FirstOrDefault()
                ?.Name
                ?? "Id";
            return retv;
        }

        public static String GetRidColumn<T>() where T : IDataObject, new() { return GetRidColumn(typeof(T)); }
        public static String GetRidColumn(Type type) {
            return FiTechBDadosExtensions.RidColumnOf[type];
        }

        public T LoadById<T>(long Id) where T : IDataObject, new() {
            if (CurrentTransaction != null) {
                return LoadById<T>(CurrentTransaction, Id);
            }
            return Access((transaction) => LoadById<T>(transaction, Id));
        }

        public T LoadByRid<T>(String RID) where T : IDataObject, new() {
            if (CurrentTransaction != null) {
                return LoadByRid<T>(CurrentTransaction, RID);
            }
            return Access((transaction) => LoadByRid<T>(transaction, RID));
        }

        public List<T> LoadAll<T>(LoadAllArgs<T> args = null) where T : IDataObject, new() {
            return Fetch<T>(args).ToList();
        }

        public List<T> LoadAll<T>(IQueryBuilder conditions, int? skip = null, int? limit = null, Expression<Func<T, object>> orderingMember = null, OrderingType ordering = OrderingType.Asc, object contextObject = null) where T : IDataObject, new() {
            if (CurrentTransaction != null) {
                return LoadAll<T>(CurrentTransaction, conditions, skip, limit, orderingMember, ordering).ToList();
            }
            return Access((transaction) => {
                return LoadAll<T>(transaction, conditions, skip, limit, orderingMember, ordering).ToList();
            });
        }

        public IEnumerable<T> Fetch<T>(LoadAllArgs<T> args = null) where T : IDataObject, new() {
            if (CurrentTransaction != null) {
                return Fetch(CurrentTransaction, args).ToList();
            }
            return Access(tsn => {
                return Fetch(tsn, args).ToList();
            });
        }

        public IEnumerable<T> Fetch<T>(IQueryBuilder conditions, int? skip = null, int? limit = null, Expression<Func<T, object>> orderingMember = null, OrderingType ordering = OrderingType.Asc, object contextObject = null) where T : IDataObject, new() {
            if (CurrentTransaction != null) {
                return Fetch<T>(CurrentTransaction, conditions, skip, limit, orderingMember, ordering, contextObject);
            }
            return Access((transaction) => {
                return Fetch<T>(transaction, conditions, skip, limit, orderingMember, ordering, contextObject);
            }, (x) => {
                WriteLog(x.Message);
                WriteLog(x.StackTrace);
                throw x;
            });
        }

        private T RunAfterLoad<T>(T target, bool isAggregateLoad, object transferObject = null) {
            if (target is IBusinessObject ibo) {
                ibo.OnAfterLoad(new DataLoadContext {
                    DataAccessor = this,
                    IsAggregateLoad = isAggregateLoad,
                    ContextTransferObject = transferObject
                });
            }
            return target;
        }

        private List<T> RunAfterLoads<T>(List<T> target, bool isAggregateLoad, object transferObject = null) {
            foreach (var a in target) {
                if (target is IBusinessObject ibo) {
                    ibo.OnAfterLoad(new DataLoadContext {
                        DataAccessor = this,
                        IsAggregateLoad = isAggregateLoad,
                        ContextTransferObject = transferObject
                    });
                }
            }
            return target;
        }

        public bool Delete<T>(IEnumerable<T> obj) where T : IDataObject, new() {
            if (CurrentTransaction != null) {
                return Delete(CurrentTransaction, obj);
            }
            return Access((transaction) => Delete(transaction, obj));
        }

        public bool Delete(IDataObject obj) {
            if (CurrentTransaction != null) {
                return Delete(CurrentTransaction, obj);
            }
            return Access((transaction) => Delete(transaction, obj));
        }

        public bool Delete<T>(Expression<Func<T, bool>> conditions) where T : IDataObject, new() {
            if (CurrentTransaction != null) {
                return Delete(CurrentTransaction, conditions);
            }
            return Access((transaction) => Delete(transaction, conditions));
        }

        #region **** BDados API ****

        int fail = 0;

        private List<Task<Object>> Workers = new List<Task<Object>>();

        private static int accessCount = 0;


        public Object ScalarQuery(IQueryBuilder qb) {
            if (CurrentTransaction != null) {
                return ScalarQuery(CurrentTransaction, qb);
            }
            return Access(transaction => ScalarQuery(transaction, qb));
        }

        public T ScalarQuery<T>(IQueryBuilder qb) {
            T retv = default(T);
            try {
                retv = (T)Query(qb).Rows[0][0];
            } catch (Exception) {
            }
            return retv;
        }

        public static IJoinBuilder MakeJoin(Action<JoinDefinition> fn) {
            var retv = new JoinObjectBuilder(fn);
            return retv;
        }

        public IQueryGenerator QueryGenerator => Plugin.QueryGenerator;

        int accessId = 0;

        public event Action<Type, IDataObject[]> OnSuccessfulSave;
        public event Action<Type, IDataObject[], Exception> OnFailedSave;
        public event Action<Type, IDataObject[]> OnDataObjectAltered;
        public event Action<Type, IDataObject[]> OnObjectsDeleted;

        public void Access(Action<BDadosTransaction> functions, Action<Exception> handler = null, IsolationLevel ilev = IsolationLevel.ReadUncommitted) {
            var i = Access<int>((transaction) => {
                functions?.Invoke(transaction);
                return 0;
            }, handler, ilev);
        }

        public T Access<T>(Func<BDadosTransaction, T> functions, Action<Exception> handler = null, IsolationLevel ilev = IsolationLevel.ReadUncommitted) {
            if (functions == null) return default(T);

            //if (transactionHandle != null && transactionHandle.State == transactionState.Open) {
            //    return functions.Invoke(transaction);
            //}
            int aid = accessId;
            return UseTransaction((transaction) => {
                aid = ++accessId;

                if (transaction.Benchmarker == null) {
                    transaction.Benchmarker = Benchmarker ?? new Benchmarker($"---- Access [{++aid}]");
                    transaction.usingExternalBenchmarker = Benchmarker != null;
                    transaction.Benchmarker.WriteToStdout = FiTechCoreExtensions.EnableStdoutLogs;
                }

                var retv = functions.Invoke(transaction);
                if (!transaction?.usingExternalBenchmarker ?? false) {
                    var total = transaction?.Benchmarker.FinalMark();
                    WriteLog(String.Format("---- Access [{0}] Finished in {1}ms", aid, total));
                } else {
                    WriteLog(String.Format("---- Access [{0}] Finished", aid));
                }
                return retv;
            }, handler, ilev);
        }

        public DataTable Query(IQueryBuilder query) {
            if (CurrentTransaction != null) {
                return Query(CurrentTransaction, query);
            }
            return Access((transaction) => {
                return Query(transaction, query);
            });
        }

        private const string RDB_SYSTEM_LOGID = "FTH:RDB";
        public void WriteLog(String s) {
            Logger?.WriteLog(s);
            Fi.Tech.WriteLine(RDB_SYSTEM_LOGID, s);
        }

        public int Execute(String str, params object[] args) {
            return Execute(Qb.Fmt(str, args));
        }

        public int Execute(IQueryBuilder query) {
            if (CurrentTransaction != null) {
                return Execute(CurrentTransaction, query);
            }
            return Access((transaction) => {
                return Execute(transaction, query);
            });
        }

        static long ConnectionTracks = 0;
        internal void OpenConnection(IDbConnection connection) {
            int attempts = DefaultMaxOpenAttempts;
            Exception ex = null;
            while (connection?.State != ConnectionState.Open && attempts-- >= 0) {
                try {
                    connection.Open();
                    if(FiTechCoreExtensions.DebugConnectionLifecycle) {
                        var stack = Environment.StackTrace;
                        lock (ActiveConnections)
                            ActiveConnections.Add((connection, Environment.StackTrace, myId));
                        var trackId = $"TRACK_CONNECTION_{++ConnectionTracks}";
                        Fi.Tech.ScheduleTask(trackId, DateTime.UtcNow + TimeSpan.FromSeconds(10), new WorkJob(() => {
                            try {
                                if (connection.State == ConnectionState.Open) {
                                    var isActive = false;
                                    lock (ActiveConnections)
                                        isActive = ActiveConnections.Any(x => x.Connection == connection);
                                    if (isActive) {

                                    } else {
                                        if (!Directory.Exists("CriticalFTHErrors")) {
                                            Directory.CreateDirectory("CriticalFTHErrors");
                                        }
                                        Debugger.Break();
                                        try {
                                            connection?.Dispose();
                                        } catch (Exception x) { }
                                    }
                                } else {
                                    Fi.Tech.Unschedule(trackId);
                                }
                            } catch(Exception x) {
                                Debugger.Break();
                            }
                            return Fi.Result();
                        }, x => Fi.Result(), s => Fi.Result()), TimeSpan.FromSeconds(10)
                        );
                    }

                    isOnline = true;
                    break;
                } catch (Exception x) {
                    isOnline = false;
                    ex = x;
                    if (x.Message.Contains("Unable to connect")) {
                        break;
                    }
                    Thread.Sleep(DefaultOpenAttemptInterval);
                }
            }
            if (connection?.State != ConnectionState.Open) {
                throw new BDadosException($"Cannot open connection to the RDBMS database service (Using {Plugin.GetType().Name}).", ex);
            }
        }

        private T UseTransaction<T>(Func<BDadosTransaction, T> func, Action<Exception> handler = null, IsolationLevel ilev = IsolationLevel.ReadUncommitted) {

            if (func == null) return default(T);

            if (CurrentTransaction != null) {
                try {
                    return func.Invoke(CurrentTransaction);
                } catch (Exception x) {
                    if (handler != null) {
                        handler?.Invoke(x);
                    } else {
                        throw new BDadosException("Error accessing the database", CurrentTransaction?.FrameHistory, null, x);
                    }
                }
                return default(T);
            }

            using (var connection = BeginTransaction(ilev)) {
                var b = CurrentTransaction.Benchmarker;
                if (FiTechCoreExtensions.EnableDebug) {
                    try {
                        int maxFrames = 6;
                        var stack = new StackTrace();
                        foreach (var f in stack.GetFrames()) {
                            var m = f.GetMethod();
                            if(m != null) {
                                var mName = m.Name;
                                var t = m.DeclaringType;
                                if(t != null) {
                                    if (t.IsNested) {
                                        t = t.DeclaringType;
                                    }
                                    var tName = t.Name;
                                    if (m.DeclaringType.Assembly != GetType().Assembly) {
                                        b.Mark($" at {tName}->{mName}");
                                        if (maxFrames-- <= 0) {
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    } catch (Exception) {
                        if (Debugger.IsAttached) {
                            Debugger.Break();
                        }
                    }
                }
                try {
                    b.Mark("Run User Code");
                    var retv = func.Invoke(CurrentTransaction);

                    WriteLog($"[{accessId}] Committing");
                    b.Mark($"[{accessId}] Begin Commit");
                    Commit();
                    b.Mark($"[{accessId}] End Commit");
                    WriteLog($"[{accessId}] Commited OK ");
                    return retv;
                } catch (Exception x) {
                    WriteLog($"[{accessId}] Begin Rollback : {x.Message} {x.StackTrace}");
                    b.Mark($"[{accessId}] Begin Rollback");
                    Rollback();
                    b.Mark($"[{accessId}] End Rollback");
                    WriteLog($"[{accessId}] Transaction rolled back ");
                    if (handler != null) {
                        handler?.Invoke(x);
                    } else {
                        throw new BDadosException("Error accessing the database", CurrentTransaction?.FrameHistory, null, x);
                    }
                } finally {
                    if (!(CurrentTransaction?.usingExternalBenchmarker ?? true)) {
                        b?.FinalMark();
                    }
                    EndTransaction();
                }

                return default(T);
            }
        }

        //private PrefixMaker prefixer = new PrefixMaker();
        /*
         * HERE BE DRAGONS
         * jk.
         * It works and it is actually really good
         * But the logic behind this is crazy,
         * it took a lot of coffee to achieve.
         */
        private static void MakeQueryAggregations(ref JoinDefinition query, Type theType, String parentAlias, String nameofThis, String pKey, PrefixMaker prefixer, bool Linear = false) {
            var membersOfT = ReflectionTool.FieldsAndPropertiesOf(theType)
                .OrderBy(x=> x.Name.GetHashCode())
                .ToList();
            //var reflectedJoinMethod = query.GetType().GetMethod("Join");

            String thisAlias = prefixer.GetAliasFor(parentAlias, nameofThis, pKey);

            // Iterating through AggregateFields
            foreach (var field in membersOfT.Where(
                    (f) =>
                        f.GetCustomAttribute<AggregateFieldAttribute>() != null)) {
                var info = field.GetCustomAttribute<AggregateFieldAttribute>();
                if (
                    (info.ExplodedFlags.Contains("root") && parentAlias != "root") ||
                    (info.ExplodedFlags.Contains("child") && parentAlias == "root")
                ) {
                    continue;
                }
                var type = info?.RemoteObjectType;
                var key = info?.ObjectKey;
                String childAlias;
                var tname = type.Name;
                var pkey = key;
                // This inversion principle might be fucktastic.
                childAlias = prefixer.GetNewAliasFor(thisAlias,
                    tname,
                    pkey);

                String OnClause = $"{thisAlias}.{key}={childAlias}.RID";

                if (!ReflectionTool.TypeContains(theType, key)) {
                    OnClause = $"{thisAlias}.RID={childAlias}.{key}";
                }
                var joh = query.Join(type, childAlias, OnClause, JoinType.LEFT);

                joh.As(childAlias);

                var qjoins = query.Joins.Where((a) => a.Alias == childAlias);
                if (qjoins.Any() && !qjoins.First().Columns.Contains(info?.RemoteField)) {
                    qjoins.First().Columns.Add(info?.RemoteField);
                    //continue;
                }

                if (field.GetCustomAttribute<AggregateFieldAttribute>() != null) {
                    joh.GetType().GetMethod("OnlyFields").Invoke(joh, new object[] { new string[] { field.GetCustomAttribute<AggregateFieldAttribute>().RemoteField } });
                }
            }

            // Iterating through AggregateFarFields
            foreach (var field in membersOfT.Where(
                    (f) =>
                        f.GetCustomAttribute<AggregateFarFieldAttribute>() != null)) {
                var memberType = ReflectionTool.GetTypeOf(field);
                var info = field.GetCustomAttribute<AggregateFarFieldAttribute>();
                if (
                    (info.ExplodedFlags.Contains("root") && parentAlias != "root") ||
                    (info.ExplodedFlags.Contains("child") && parentAlias == "root")
                ) {
                    continue;
                }
                String childAlias = prefixer.GetAliasFor(thisAlias, info.ImediateType.Name, info.ImediateKey);
                String farAlias = prefixer.GetAliasFor(childAlias, info.FarType.Name, info.FarKey);

                var qimediate = query.Joins.Where((j) => j.Alias == childAlias);
                if (!qimediate.Any()) {

                    string OnClause = $"{thisAlias}.{info.ImediateKey}={childAlias}.RID";
                    // This inversion principle will be fucktastic.
                    // But has to be this way for now.
                    if (!ReflectionTool.TypeContains(theType, info.ImediateKey)) {
                        OnClause = $"{thisAlias}.RID={childAlias}.{info.ImediateKey}";
                    }
                    if (query.Joins.Where((a) => a.Alias == childAlias).Any())
                        continue;

                    var joh1 = query.Join(info.ImediateType, childAlias, OnClause, JoinType.LEFT);

                    // Parent Alias is typeof(T).Name
                    // Child Alias is field.Name
                    // The ultra supreme gimmick mode reigns supreme here.
                    joh1.As(childAlias);
                    joh1.OnlyFields(new string[] { info.FarKey });

                }

                var qfar = query.Joins.Where((j) => j.Alias == farAlias);
                if (qfar.Any() && !qfar.First().Columns.Contains(info.FarField)) {
                    qfar.First().Columns.Add(info.FarField);
                    continue;
                } else {
                    String OnClause2 = $"{childAlias}.{info.FarKey}={farAlias}.RID";
                    // This inversion principle will be fucktastic.
                    // But has to be this way for now.
                    if (!ReflectionTool.TypeContains(info.ImediateType, info.FarKey)) {
                        OnClause2 = $"{childAlias}.RID={farAlias}.{info.FarKey}";
                    }

                    var joh2 = query.Join(info.FarType, farAlias, OnClause2, JoinType.LEFT);
                    // Parent Alias is typeof(T).Name
                    // Child Alias is field.Name
                    // The ultra supreme gimmick mode reigns supreme here.
                    joh2.As(farAlias);
                    joh2.OnlyFields(new string[] { info.FarField });
                }
            }
            // We want to skip aggregate objects and lists 
            // When doing linear aggregate loads
            // The linear option is just to provide faster
            // and shallower information.
            if (Linear)
                return;
            // Iterating through AggregateObjects
            foreach (var field in membersOfT.Where(
                    (f) => f.GetCustomAttribute<AggregateObjectAttribute>() != null)) {
                var memberType = ReflectionTool.GetTypeOf(field);
                var type = ReflectionTool.GetTypeOf(field);
                var key = field.GetCustomAttribute<AggregateObjectAttribute>()?.ObjectKey;
                var info = field.GetCustomAttribute<AggregateObjectAttribute>();
                if (
                    (info.ExplodedFlags.Contains("root") && parentAlias != "root") ||
                    (info.ExplodedFlags.Contains("child") && parentAlias == "root")
                ) {
                    continue;
                }
                String childAlias;
                var tname = type.Name;
                var pkey = key;
                // This inversion principle might be fucktastic.
                childAlias = prefixer.GetAliasFor(thisAlias,
                    tname,
                    pkey);

                String OnClause = $"{thisAlias}.{key}={childAlias}.RID";

                if (!ReflectionTool.TypeContains(theType, key)) {
                    OnClause = $"{thisAlias}.RID={childAlias}.{key}";
                }

                var joh = query.Join(type, childAlias, OnClause, JoinType.LEFT);

                joh.As(childAlias);

                var qjoins = query.Joins.Where((a) => a.Alias == childAlias);
                if (qjoins.Any()) {
                    qjoins.First().Columns.AddRange(
                        ReflectionTool.FieldsWithAttribute<FieldAttribute>(memberType)
                            .Select(m => m.Name)
                            .Where(i => !qjoins.First().Columns.Contains(i))
                    );
                    //continue;
                }

                var ago = field.GetCustomAttribute<AggregateObjectAttribute>();
                if (ago != null) {
                    MakeQueryAggregations(ref query, type, thisAlias, tname, pkey, prefixer);
                }
            }
            // Iterating through AggregateLists
            foreach (var field in membersOfT.Where(
                    (f) =>
                        f.GetCustomAttribute<AggregateListAttribute>() != null)) {
                var memberType = ReflectionTool.GetTypeOf(field);
                var info = field.GetCustomAttribute<AggregateListAttribute>();
                if (
                     (info.ExplodedFlags.Contains("root") && parentAlias != "root") ||
                     (info.ExplodedFlags.Contains("child") && parentAlias == "root")
                ) {
                    continue;
                }
                String childAlias = prefixer.GetAliasFor(thisAlias, info.RemoteObjectType.Name, info.RemoteField);

                String OnClause = $"{childAlias}.{info.RemoteField}={thisAlias}.RID";
                // Yuck
                if (!ReflectionTool.TypeContains(info.RemoteObjectType, info.RemoteField)) {
                    OnClause = $"{childAlias}.RID={thisAlias}.{info.RemoteField}";
                }
                var joh = query.Join(info.RemoteObjectType, childAlias, OnClause, JoinType.RIGHT);
                // The ultra supreme gimmick mode reigns supreme here.
                joh.GetType().GetMethod("As").Invoke(joh, new object[] { childAlias });

                var qjoins = query.Joins.Where((a) => a.Alias == childAlias);
                if (qjoins.Any()) {
                    qjoins.First().Columns.AddRange(
                        ReflectionTool.FieldsWithAttribute<FieldAttribute>(info.RemoteObjectType)
                            .Select(m => m.Name)
                            .Where(i => !qjoins.First().Columns.Contains(i))
                    );
                    //continue;
                }

                if (!Linear) {
                    MakeQueryAggregations(ref query, info.RemoteObjectType, thisAlias, info.RemoteObjectType.Name, info.RemoteField, prefixer);
                }
            }
        }


        private static void MakeBuildAggregations(BuildParametersHelper build, Type theType, String parentAlias, String nameofThis, String pKey, PrefixMaker prefixer, bool Linear = false) {
            // Don't try this at home kids.
            var membersOfT = ReflectionTool.FieldsAndPropertiesOf(theType);

            String thisAlias = prefixer.GetAliasFor(parentAlias, nameofThis, pKey);
            // Iterating through AggregateFields
            foreach (var field in membersOfT.Where((f) => f.GetCustomAttribute<AggregateFieldAttribute>() != null)) {
                var memberType = ReflectionTool.GetTypeOf(field);
                var info = field.GetCustomAttribute<AggregateFieldAttribute>();
                if (
                    (info.ExplodedFlags.Contains("root") && parentAlias != "root") ||
                    (info.ExplodedFlags.Contains("child") && parentAlias == "root")
                ) {
                    continue;
                }
                String childAlias = prefixer.GetAliasFor(thisAlias, info.RemoteObjectType.Name, info.ObjectKey);
                build.AggregateField(thisAlias, childAlias, info.RemoteField, field.Name);
            }
            // Iterating through AggregateFarFields
            foreach (var field in membersOfT.Where(
                    (f) =>
                        f.GetCustomAttribute<AggregateFarFieldAttribute>() != null)) {
                var memberType = ReflectionTool.GetTypeOf(field);
                var info = field.GetCustomAttribute<AggregateFarFieldAttribute>();
                if (
                    (info.ExplodedFlags.Contains("root") && parentAlias != "root") ||
                    (info.ExplodedFlags.Contains("child") && parentAlias == "root")
                ) {
                    continue;
                }
                String childAlias = prefixer.GetAliasFor(thisAlias, info.ImediateType.Name, info.ImediateKey);
                String farAlias = prefixer.GetAliasFor(childAlias, info.FarType.Name, info.FarKey);
                build.AggregateField(thisAlias, farAlias, info.FarField, field.Name);
            }
            // Iterating through ComputeFields
            //foreach (var field in membersOfT.Where((f) => ReflectionTool.GetTypeOf(f) == typeof(ComputeField))) {
            //    var memberType = ReflectionTool.GetTypeOf(field);
            //    String childAlias = prefixer.GetAliasFor(thisAlias, field.Name);
            //    if (field is FieldInfo) {
            //        build.ComputeField(thisAlias, field.Name.Replace("Compute", ""), (ComputeField)((FieldInfo)field).GetValue(null));
            //    }
            //    if (field is PropertyInfo) {
            //        build.ComputeField(thisAlias, field.Name.Replace("Compute", ""), (ComputeField)((PropertyInfo)field).GetValue(null));
            //    }
            //}
            // We want to skip aggregate lists 
            // When doing linear aggregate loads
            // To avoid LIMIT ORDER BY MySQL dead-lock
            if (Linear)
                return;
            // Iterating through AggregateObjects
            foreach (var field in membersOfT.Where((f) => f.GetCustomAttribute<AggregateObjectAttribute>() != null)) {
                var memberType = ReflectionTool.GetTypeOf(field);
                var info = field.GetCustomAttribute<AggregateObjectAttribute>();
                if (
                     (info.ExplodedFlags.Contains("root") && parentAlias != "root") ||
                     (info.ExplodedFlags.Contains("child") && parentAlias == "root")
                ) {
                    continue;
                }
                String childAlias = prefixer.GetAliasFor(thisAlias, memberType.Name, info.ObjectKey);
                build.AggregateObject(thisAlias, childAlias, field.Name);
                if (!Linear) {
                    MakeBuildAggregations(build, ReflectionTool.GetTypeOf(field), thisAlias, memberType.Name, info.ObjectKey, prefixer);
                }
            }
            // Iterating through AggregateLists
            foreach (var field in membersOfT.Where((f) => f.GetCustomAttribute<AggregateListAttribute>() != null)) {
                var memberType = ReflectionTool.GetTypeOf(field);
                var info = field.GetCustomAttribute<AggregateListAttribute>();
                if (
                    (info.ExplodedFlags.Contains("root") && parentAlias != "root") ||
                    (info.ExplodedFlags.Contains("child") && parentAlias == "root")
                ) {
                    continue;
                }
                String childAlias = prefixer.GetAliasFor(thisAlias, info.RemoteObjectType.Name, info.RemoteField);
                build.AggregateList(thisAlias, childAlias, field.Name);
                if (!Linear) {
                    MakeBuildAggregations(build, info.RemoteObjectType, thisAlias, info.RemoteObjectType.Name, info.RemoteField, prefixer);
                }
            }
        }

        public List<T> AggregateLoad<T>(LoadAllArgs<T> args = null) where T : IDataObject, new() {

            if (CurrentTransaction != null) {
                return AggregateLoad(
                    CurrentTransaction, args);
            }
            return Access((transaction) => AggregateLoad(transaction, args));
        }



        public bool DeleteWhereRidNotIn<T>(Expression<Func<T, bool>> cnd, List<T> list) where T : IDataObject, new() {
            if (CurrentTransaction != null) {
                return DeleteWhereRidNotIn(CurrentTransaction, cnd, list);
            }
            return Access((transaction) => DeleteWhereRidNotIn(transaction, cnd, list));
        }
        ~RdbmsDataAccessor() {
            Dispose();
        }
        public void Dispose() {
            if(_currentTransaction != null) {
                foreach(var v in _currentTransaction) {
                    if(v.Value != null) {
                        v.Value?.Dispose();
                    }
                }
            }
            if(ActiveConnections != null) {
                foreach(var v in ActiveConnections) {
                    try {
                        v.Connection?.Dispose();
                    } catch(Exception x) { }
                }
            }
        }
        #endregion *****************
        //
        #region Default Transaction Using Core Functions.
        public T ForceExist<T>(BDadosTransaction transaction, Func<T> Default, IQueryBuilder qb) where T : IDataObject, new() {
            var f = LoadAll<T>(transaction, qb, null, 1);
            if (f.Any()) {
                return f.First();
            } else {
                T quickSave = Default();
                SaveItem(quickSave);
                return quickSave;
            }
        }

        public bool SaveList<T>(BDadosTransaction transaction, List<T> rs, bool recoverIds = false) where T : IDataObject {
            transaction.Step();
            bool retv = true;

            if (rs.Count == 0)
                return true;
            //if (rs.Count == 1) {
            //    return SaveItem(transaction, rs.First());
            //}
            for (int it = 0; it < rs.Count; it++) {
                if (rs[it].RID == null) {
                    rs[it].RID = new RID().ToString();
                }
            }

            rs.ForEach(item => {
                item.IsPersisted = false;
                if (!item.IsReceivedFromSync) {
                    item.UpdatedTime = Fi.Tech.GetUtcTime();
                }
            });
            DateTime d1 = DateTime.UtcNow;
            List<T> conflicts = new List<T>();
            QueryReader(transaction, Plugin.QueryGenerator.QueryIds(rs), reader => {
                while (reader.Read()) {
                    var vId = (long?) Convert.ChangeType(reader[0], typeof(long));
                    var vRid = (string) Convert.ChangeType(reader[1], typeof(string));
                    var rowEquivalentObject = rs.FirstOrDefault(item => item.Id == vId || item.RID == vRid);
                    if (rowEquivalentObject != null) {
                        rowEquivalentObject.Id = vId ?? rowEquivalentObject.Id;
                        rowEquivalentObject.RID = vRid ?? rowEquivalentObject.RID;
                        rowEquivalentObject.IsPersisted = true;
                    }
                }
            });
            var elaps = DateTime.UtcNow - d1;

            var members = ReflectionTool.FieldsAndPropertiesOf(typeof(T))
                .Where(t => t.GetCustomAttribute<FieldAttribute>() != null)
                .ToList();
            int i2 = 0;
            int cut = Math.Max(500, 37000 / (7 * (members.Count + 1)));
            int rst = 0;
            List<T> temp;

            if (rs.Count > cut) {
                temp = new List<T>();
                temp.AddRange(rs.OrderBy(it => it.IsPersisted));
            } else {
                temp = rs;
            }
            List<Exception> failedSaves = new List<Exception>();
            List<IDataObject> successfulSaves = new List<IDataObject>();
            List<IDataObject> failedObjects = new List<IDataObject>();
            transaction?.Benchmarker.Mark($"Begin SaveList<{typeof(T).Name}> process");
            //WorkQueuer wq = rs.Count > cut ? new WorkQueuer("SaveList_Annonymous_Queuer", 1, true) : null;

            while (i2 * cut < rs.Count) {
                int i = i2;
                Action WorkFn = () => {
                    List<T> sub;
                    lock (temp)
                        sub = temp.Skip(i * cut).Take(Math.Min(rs.Count - (i * cut), cut)).ToList();
                    var inserts = sub.Where(it => !it.IsPersisted).ToList();
                    var updates = sub.Where(it => it.IsPersisted).ToList();
                    if (inserts.Count > 0) {
                        try {
                            transaction?.Benchmarker.Mark($"Generate MultiInsert Query for {inserts.Count} {typeof(T).Name}");
                            var query = Plugin.QueryGenerator.GenerateMultiInsert<T>(inserts, false);
                            transaction?.Benchmarker.Mark($"Execute MultiInsert Query {inserts.Count} {typeof(T).Name}");
                            lock (transaction)
                                rst += Execute(transaction, query);
                            lock (successfulSaves)
                                successfulSaves.AddRange(inserts.Select(a => (IDataObject)a));
                        } catch (Exception x) {
                            if(OnFailedSave != null) {
                                Fi.Tech.FireAndForget(async () => {
                                    await Task.Yield();
                                    OnFailedSave?.Invoke(typeof(T), inserts.Select(a => (IDataObject)a).ToArray(), x);
                                });
                            }

                            lock (failedSaves)
                                failedSaves.Add(x);
                        }
                        if (recoverIds) {
                            var queryIds = Query(transaction, QueryGenerator.QueryIds(inserts));
                            foreach (DataRow dr in queryIds.Rows) {
                                var psave = inserts.FirstOrDefault(it => it.RID == dr[1] as String);
                                if (psave != null) {
                                    psave.Id = Int64.Parse(dr[0] as String);
                                }
                            }
                        }
                    }

                    if (updates.Count > 0) {
                        try {
                            transaction?.Benchmarker.Mark($"Generate MultiUpdate Query for {updates.Count} {typeof(T).Name}");
                            var query = Plugin.QueryGenerator.GenerateMultiUpdate(updates);
                            transaction?.Benchmarker.Mark($"Execute MultiUpdate Query for {updates.Count} {typeof(T).Name}");
                            lock (transaction)
                                rst += Execute(transaction, query);
                            lock (successfulSaves)
                                successfulSaves.AddRange(updates.Select(a => (IDataObject)a));
                        } catch (Exception x) {
                            if(OnFailedSave != null) {
                                Fi.Tech.FireAndForget(async () => {
                                    await Task.Yield();
                                    OnFailedSave?.Invoke(typeof(T), updates.Select(a => (IDataObject)a).ToArray(), x);
                                });
                            }
                            lock (failedSaves)
                                failedSaves.Add(x);
                        }
                    }
                };

                //if (rs.Count > cut) {
                //    wq.Enqueue(WorkFn);
                //} else {
                WorkFn.Invoke();
                //}

                i2++;
            }
            //wq?.Stop(true);
            transaction?.Benchmarker.Mark($"End SaveList<{typeof(T).Name}> process");

            transaction?.Benchmarker.Mark($"Dispatch Successful Save events {typeof(T).Name}");
            if (successfulSaves.Any()) {
                //if (recoverIds) {
                //    var q = Query(transaction, QueryGenerator.QueryIds(rs));
                //    foreach (DataRow dr in q.Rows) {
                //        successfulSaves.FirstOrDefault(it => it.RID == dr[1] as String).Id = Int64.Parse(dr[0] as String);
                //    }
                //    failedObjects.AddRange(successfulSaves.Where(a => a.Id <= 0).Select(a => (IDataObject)a));
                //    successfulSaves.RemoveAll(a => a.Id <= 0);
                //}
                int newHash = 0;
                successfulSaves.ForEach(it => {
                    newHash = it.SpFthComputeDataFieldsHash();
                    if (it.PersistedHash != newHash) {
                        it.PersistedHash = newHash;
                        it.AlteredBy = IDataObjectExtensions.localInstanceId;
                    }
                });
                transaction.NotifyChange(successfulSaves.ToArray());
                if(OnSuccessfulSave!=null) {
                    Fi.Tech.FireAndForget(async () => {
                        await Task.Yield();
                        OnSuccessfulSave?.Invoke(typeof(T), successfulSaves.ToArray());
                    }, async ex => {
                        await Task.Yield();
                        Fi.Tech.Throw(ex);
                    });
                }
            }
            transaction?.Benchmarker.Mark($"SaveList all done");
            if (failedSaves.Any()) {
                throw new BDadosException($"Not everything could be saved list of type {typeof(T).Name}", transaction.FrameHistory, failedObjects, new AggregateException(failedSaves));
            }
            if (failedObjects.Any()) {
                var ex = new BDadosException("Some objects did not persist correctly", transaction.FrameHistory, failedObjects, null);
                if(OnFailedSave!=null) {
                    Fi.Tech.FireAndForget(async () => {
                        await Task.Yield();
                        OnFailedSave?.Invoke(typeof(T), failedObjects.Select(a => (IDataObject)a).ToArray(), ex);
                    });
                }
            }

            return retv;
        }

        public Object ScalarQuery(BDadosTransaction transaction, IQueryBuilder qb) {
            transaction.Step();
            Object retv = null;
            try {
                retv = Query(transaction, qb).Rows[0][0];
            } catch (Exception) {
            }
            return retv;
        }

        public bool DeleteWhereRidNotIn<T>(BDadosTransaction transaction, Expression<Func<T, bool>> cnd, List<T> list) where T : IDataObject, new() {
            int retv = 0;
            if (list == null)
                return true;

            var id = GetIdColumn<T>();
            var rid = GetRidColumn<T>();
            IQueryBuilder query = QueryGenerator.GenerateSelectAll<T>().Append("WHERE");
            if (cnd != null) {
                PrefixMaker pm = new PrefixMaker();
                query.Append($"{rid} IN (SELECT {rid} FROM (SELECT {rid} FROM {typeof(T).Name} AS {pm.GetAliasFor("root", typeof(T).Name, String.Empty)} WHERE ");
                query.Append(new ConditionParser(pm).ParseExpression<T>(cnd));
                query.Append(") sub)");
            }
            if (list.Count > 0) {

                query.Append("AND");
                query.Append(Qb.NotIn(rid, list, l => l.RID));
                //for (var i = 0; i < list.Count; i++) {
                //    query.Append($"@{IntEx.GenerateShortRid()}", list[i].RID);
                //    if (i < list.Count - 1)
                //        query.Append(",");
                //}
                //query.Append(")");
            }

            var results = Query<T>(transaction, query);
            if (results.Any()) {
                OnObjectsDeleted?.Invoke(typeof(T), results.Select(t => t as IDataObject).ToArray());
                var query2 = Qb.Fmt($"DELETE FROM {typeof(T).Name} WHERE ") + Qb.In(rid, results, r => r.RID);
                retv = Execute(transaction, query2);
                return retv > 0;
            }
            return true;

            //var id = GetIdColumn<T>();
            //var rid = GetRidColumn<T>();
            //var query = Qb.Fmt($"DELETE FROM {typeof(T).Name} WHERE ");
            //if (cnd != null) {
            //    PrefixMaker pm = new PrefixMaker();
            //    query.Append($"{rid} IN (SELECT {rid} FROM (SELECT {rid} FROM {typeof(T).Name} AS {pm.GetAliasFor("root", typeof(T).Name, String.Empty)} WHERE ");
            //    query.Append(new ConditionParser(pm).ParseExpression<T>(cnd));
            //    query.Append(") sub)");
            //}
            //if (list.Count > 0) {
            //    query.Append($"AND {rid} NOT IN (");
            //    for (var i = 0; i < list.Count; i++) {
            //        query.Append($"@{IntEx.GenerateShortRid()}", list[i].RID);
            //        if (i < list.Count - 1)
            //            query.Append(",");
            //    }
            //    query.Append(")");
            //}
            //retv = Execute(transaction, query);
            //return retv > 0;
        }

        public bool SaveList<T>(List<T> rs, bool recoverIds = false) where T : IDataObject {
            if (CurrentTransaction != null) {
                return SaveList<T>(CurrentTransaction, rs, recoverIds);
            }
            return Access((transaction) => {
                return SaveList<T>(transaction, rs, recoverIds);
            });
        }

        public List<IDataObject> LoadUpdatedItemsSince(IEnumerable<Type> types, DateTime dt) {
            if (CurrentTransaction != null) {
                return LoadUpdatedItemsSince(CurrentTransaction, types, dt);
            } else {
                return Access((transaction) => {
                    return LoadUpdatedItemsSince(transaction, types, dt);
                });
            }
        }

        public List<IDataObject> LoadUpdatedItemsSince(BDadosTransaction transaction, IEnumerable<Type> types, DateTime dt) {
            var workingTypes = types.Where(t => t.Implements(typeof(IDataObject))).ToList();
            var fields = new Dictionary<Type, MemberInfo[]>();
            foreach (var type in workingTypes) {
                fields[type] = ReflectionTool.FieldsWithAttribute<FieldAttribute>(type).ToArray();
            }

            var query = Plugin.QueryGenerator.GenerateGetStateChangesQuery(workingTypes, fields, dt);

            using (var command = transaction.CreateCommand()) {
                VerboseLogQueryParameterization(transaction, query);
                query.ApplyToCommand(command, Plugin.ProcessParameterValue);
                transaction?.Benchmarker?.Mark($"Execute Query <{query.Id}>");
                using (var reader = command.ExecuteReader()) {
                    return BuildStateUpdateQueryResult(transaction, reader, workingTypes, fields);
                }
            }
        }

        private void SortTypesByDep(List<Type> workingTypes) {
            Dictionary<Type, Type[]> DepTrees = new Dictionary<Type, Type[]>();

            foreach (var a in workingTypes) {
                DepTrees[a] = TypeDepScore.ExplodeDependencyTree(a).ToArray();
            }
            foreach (var a in workingTypes) {
                foreach (var b in workingTypes) {

                    var aDependsOnB = DepTrees[a].Any(t => t == b);
                    var bDependsOnA = DepTrees[b].Any(t => t == a);

                    if (aDependsOnB && bDependsOnA)
                        Console.WriteLine($"Cross dependency: {a.Name} <-> {b.Name}");
                }
            }
            List<Type> finalList = new List<Type>();
            List<Type> tempList = new List<Type>(workingTypes);
            while (finalList.Count < workingTypes.Count) {
                for (int i = 0; i < tempList.Count; i++) {
                    if (!finalList.Contains(tempList[i])) {
                        // Check if all items in dependency tree meets
                        // - Is the type itself (self-dependency) or
                        // - Is already in the final list or
                        // - Is not in the temp list (dependency blatantly missing)
                        // If all dependencies meet, then tempList[i] is clear to enter the final list 
                        if (DepTrees[tempList[i]].All(t => t == tempList[i] || finalList.Contains(t) || !tempList.Contains(t))) {
                            finalList.Add(tempList[i]);
                        }
                    }
                }
                tempList.RemoveAll(i => finalList.Contains(i));
            }
            workingTypes.Clear();
            workingTypes.AddRange(finalList);
        }

        public void SendLocalUpdates(IEnumerable<Type> types, DateTime dt, Stream stream) {
            if (CurrentTransaction != null) {
                SendLocalUpdates(CurrentTransaction, types, dt, stream);
            } else {
                Access(tsn => SendLocalUpdates(tsn, types, dt, stream));
            }
        }
        public void ReceiveRemoteUpdatesAndPersist(IEnumerable<Type> types, Stream stream) {
            if (CurrentTransaction != null) {
                ReceiveRemoteUpdatesAndPersist(CurrentTransaction, types, stream);
            } else {
                Access(tsn => ReceiveRemoteUpdatesAndPersist(tsn, types, stream));
            }
        }

        public void SendLocalUpdates(BDadosTransaction transaction, IEnumerable<Type> types, DateTime dt, Stream stream) {
            var workingTypes = types.Where(t => !t.IsInterface && t.GetCustomAttribute<ViewOnlyAttribute>() == null && !t.IsGenericType && t.Implements(typeof(IDataObject))).ToList();

            SortTypesByDep(workingTypes);

            var fields = new Dictionary<Type, MemberInfo[]>();
            foreach (var type in workingTypes) {
                fields[type] = ReflectionTool.FieldsWithAttribute<FieldAttribute>(type).ToArray();
            }
            var memberTypeOf = new Dictionary<MemberInfo, Type>();
            foreach (var typeMembers in fields) {
                foreach (var member in typeMembers.Value) {
                    memberTypeOf[member] = ReflectionTool.GetTypeOf(member);
                }
            }
            var query = Plugin.QueryGenerator.GenerateGetStateChangesQuery(workingTypes, fields, dt);

            var cmd = transaction.CreateCommand("set net_write_timeout=99999; set net_read_timeout=99999");
            cmd.ExecuteNonQuery();

            using (var command = transaction.CreateCommand()) {
                command.CommandTimeout = 999999;
                VerboseLogQueryParameterization(transaction, query);
                query.ApplyToCommand(command, Plugin.ProcessParameterValue);
                transaction?.Benchmarker?.Mark($"@SendLocalUpdates Execute Query <{query.Id}>");
                using (var reader = command.ExecuteReader()) {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024 * 64, true)) {
                        object[] values = new object[reader.FieldCount];
                        transaction?.Benchmarker?.Mark("Begin transmit data");
                        BinaryFormatter bf = new BinaryFormatter();
                        int readRows = 0;
                        try {
                            while (true) {
                                if (transaction?.ConnectionState == ConnectionState.Closed) {
                                    Debugger.Break();
                                }
                                if (reader.IsClosed || !reader.Read()) {
                                    break;
                                }
                                readRows++;
                                // 0x09 is Tab in the ASCII table,
                                // this character is chosen because it's 100% sure it will not
                                // appear in the JSON serialized values
                                reader.GetValues(values);
                                var type = workingTypes.FirstOrDefault(t => t.Name == values[0] as string);
                                if (type == null) {
                                    continue;
                                }
                                for (int i = 0; i < fields[type].Length; i++) {
                                    values[i + 1] = ReflectionTool.TryCast(values[i + 1], memberTypeOf[fields[type][i]]);
                                }
                                // +1 because we need to add the type name too
                                var outv = new object[fields[type].Length + 1];
                                Array.Copy(values, 0, outv, 0, outv.Length);
                                writer.WriteLine(JsonConvert.SerializeObject(outv));
                                //writer.WriteLine(String.Join(((char) 0x09).ToString(), values.Select(v => JsonConvert.SerializeObject(v))));
                            }
                        } catch (Exception x) {
                            var elaps = transaction?.Benchmarker?.Mark("End data transmission");
                            Console.WriteLine($"Error in {elaps}ms");
                            throw x;
                        }
                        transaction?.Benchmarker?.Mark($"End data transmission {readRows} items");
                    }
                }
            }
        }

        public IEnumerable<IDataObject> ReceiveRemoteUpdates(IEnumerable<Type> types, Stream stream) {
            var workingTypes = types.Where(t => !t.IsInterface && t.GetCustomAttribute<ViewOnlyAttribute>() == null && !t.IsGenericType && t.Implements(typeof(IDataObject))).ToList();
            var fields = new Dictionary<Type, MemberInfo[]>();
            foreach (var type in workingTypes) {
                fields[type] = ReflectionTool.FieldsWithAttribute<FieldAttribute>(type).ToArray();
            }
            var memberTypeOf = new Dictionary<MemberInfo, Type>();
            foreach (var typeMembers in fields) {
                foreach (var member in typeMembers.Value) {
                    memberTypeOf[member] = ReflectionTool.GetTypeOf(member);
                }
            }

            var cache = new Queue<IDataObject>();
            var objAssembly = new WorkQueuer("rcv_updates_objasm", 32, true);

            using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024 * 1024 * 8)) {
                String line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine())) {
                    var values = JsonConvert.DeserializeObject<object[]>(line);
                    objAssembly.Enqueue(async () => {
                        await Task.Yield();
                        var v = values;
                        Type type = types.FirstOrDefault(t => t.Name == values[0] as String);
                        if (type == null)
                            return;
                        var instance = Activator.CreateInstance(type);
                        var ft = fields[type];
                        for (int i = 0; i < fields[type].Length; i++) {
                            ReflectionTool.SetMemberValue(ft[i], instance, v[i + 1]);
                        }
                        var add = instance as IDataObject;
                        if (add != null) {
                            lock (cache) {
                                cache.Enqueue(add);
                            }
                        } else {
                            Debugger.Break();
                        }
                        //yield return instance as IDataObject;
                    });
                }
            }
            while (cache.Count > 0) {
                lock (cache) {
                    var ret = cache.Dequeue();
                    if (ret != null) {
                        yield return ret;
                    } else {
                        Debugger.Break();
                    }
                }
            }

            while (objAssembly.WorkDone < objAssembly.TotalWork) {
                while (cache.Count > 0) {
                    lock (cache) {
                        var ret = cache.Dequeue();
                        if (ret != null) {
                            yield return ret;
                        } else {
                            Debugger.Break();
                        }
                        lock (cache) {
                        }
                    }
                }
            }
            objAssembly.Stop(true).Wait();
        }

        public void ReceiveRemoteUpdatesAndPersist(BDadosTransaction transaction, IEnumerable<Type> types, Stream stream) {

            var cache = new List<IDataObject>();
            int maxCacheLenBeforeFlush = 5000;
            var persistenceQueue = new WorkQueuer("rcv_updates_persist", 1, true);

            Action flushAndPersist = () => {
                lock (cache) {
                    var persistenceBatch = new List<IDataObject>(cache);
                    cache.Clear();
                    persistenceQueue.Enqueue(async () => {
                        await Task.Yield();
                        var grouping = persistenceBatch.GroupBy(item => item.GetType());
                        foreach (var g in grouping) {
                            var listOfType = g.ToList();
                            Console.WriteLine($"Saving batch of type {listOfType.First().GetType().Name} {listOfType.Count} items");
                            listOfType.ForEach(i => i.IsReceivedFromSync = true);
                            SaveList(transaction, listOfType, false);
                        }
                    }, async x => {
                        await Task.Yield();
                        Console.WriteLine($"Error persisting batch {x.Message}");
                    });
                }
            };

            foreach (var instance in ReceiveRemoteUpdates(types, stream)) {
                lock (cache) {
                    cache.Add(instance);
                }
                if (persistenceQueue.TotalWork - persistenceQueue.WorkDone > 2) {
                    Thread.Sleep(100);
                }
                if (cache.Count >= maxCacheLenBeforeFlush) {
                    flushAndPersist();
                }
            }

            flushAndPersist();
            persistenceQueue.Stop(true).Wait();
        }

        public bool Delete<T>(BDadosTransaction transaction, Expression<Func<T, bool>> conditions) where T : IDataObject, new() {
            transaction.Step();
            bool retv = false;
            var prefixMaker = new PrefixMaker();
            var cnd = new ConditionParser(prefixMaker).ParseExpression<T>(conditions);
            var rid = GetRidColumn<T>();

            var query = Qb.Fmt($"DELETE FROM {typeof(T).Name} WHERE {rid} in (SELECT {rid} FROM (SELECT {rid} FROM {typeof(T).Name} tba WHERE ") + cnd + Qb.Fmt(") a);");
            retv = Execute(transaction, query) > 0;
            return retv;
        }

        private void VerboseLogQueryParameterization(BDadosTransaction transaction, IQueryBuilder query) {
            if(!FiTechCoreExtensions.EnabledSystemLogs[RDB_SYSTEM_LOGID]) {
                return;
            }
            String QueryText = query.GetCommandText();
            WriteLog($"[{accessId}] -- Query <{query.Id}>:\n {QueryText}");
            transaction?.Benchmarker?.Mark($"[{accessId}] Prepare Statement <{query.Id}>");
            // Adiciona os parametros
            foreach (KeyValuePair<String, Object> param in query.GetParameters()) {

                var pval = $"'{param.Value?.ToString() ?? "null"}'";
                if (param.Value is DateTime || param.Value is DateTime? && ((DateTime?)param.Value).HasValue) {
                    pval = ((DateTime)param.Value).ToString("yyyy-MM-dd HH:mm:ss");
                    pval = $"'{pval}'";
                }

                WriteLog($"[{accessId}] SET @{param.Key} = {pval} -- {param.Value?.GetType()?.Name}");
            }
        }

        public List<T> Query<T>(BDadosTransaction transaction, IQueryBuilder query) where T : new() {
            transaction.Step();

            if (query == null || query.GetCommandText() == null) {
                return new List<T>();
            }
            var tName = typeof(T).Name;
            DateTime Inicio = DateTime.Now;
            using (var command = transaction.CreateCommand()) {
                command.CommandTimeout = Plugin.CommandTimeout;
                query.ApplyToCommand(command, Plugin.ProcessParameterValue);
                VerboseLogQueryParameterization(transaction, query);
                // --
                List<T> retv;
                transaction?.Benchmarker?.Mark($"[{accessId}] Enter lock region");
                lock (transaction) {
                    transaction?.Benchmarker?.Mark($"[{accessId}] Execute Query<{tName}> <{query.Id}>");
                    retv = GetObjectList<T>(transaction, command);
                    transaction?.Benchmarker?.Mark($"[{accessId}] Build<{tName}> completed <{query.Id}>");
                }
                if (retv == null) {
                    throw new Exception("Null list generated");
                }
                var elaps = transaction?.Benchmarker?.Mark($"[{accessId}] Built List <{query.Id}> Size: {retv.Count}");
                transaction?.Benchmarker?.Mark($"[{accessId}] Avg Build speed: {((double)elaps / (double)retv.Count).ToString("0.00")}ms/item");

                try {
                    int nResults = 0;
                    nResults = retv.Count;
                    WriteLog($"[{accessId}] -------- Query<{tName}> <{query.Id}> [OK] ({nResults} results) [{elaps} ms]");
                    return retv;
                } catch (Exception x) {
                    WriteLog($"[{accessId}] -------- Error<{tName}> <{query.Id}>: {x.Message} ([{DateTime.Now.Subtract(Inicio).TotalMilliseconds} ms]");
                    WriteLog(x.Message);
                    WriteLog(x.StackTrace);
                    throw x;
                } finally {
                    WriteLog("------------------------------------");
                }
            }
        }

        public T LoadById<T>(BDadosTransaction transaction, long Id) where T : IDataObject, new() {
            transaction.Step();

            var id = GetIdColumn(typeof(T));
            return LoadAll<T>(transaction, new Qb().Append($"{id}=@id", Id), null, 1).FirstOrDefault();
        }

        public T LoadByRid<T>(BDadosTransaction transaction, String RID) where T : IDataObject, new() {
            transaction.Step();

            var rid = GetRidColumn(typeof(T));
            return LoadAll<T>(transaction, new Qb().Append($"{rid}=@rid", RID), null, 1).FirstOrDefault();
        }

        public List<T> LoadAll<T>(BDadosTransaction transaction, LoadAllArgs<T> args = null) where T : IDataObject, new() {
            transaction.Step();

            return Fetch<T>(transaction, args).ToList();
        }

        public List<T> LoadAll<T>(BDadosTransaction transaction, IQueryBuilder conditions, int? skip = null, int? limit = null, Expression<Func<T, object>> orderingMember = null, OrderingType ordering = OrderingType.Asc, object contextObject = null) where T : IDataObject, new() {
            transaction.Step();

            return Fetch<T>(transaction, conditions, skip, limit, orderingMember, ordering, contextObject).ToList();
        }

        public bool Delete(BDadosTransaction transaction, IDataObject obj) {
            transaction.Step();

            bool retv = false;

            //var id = GetIdColumn(obj.GetType());
            var rid = obj.RID;
            var ridcol = FiTechBDadosExtensions.RidColumnOf[obj.GetType()];
            string ridname = "RID";
            if (ridcol != null) {
                ridname = ridcol;
            }
            OnObjectsDeleted?.Invoke(obj.GetType(), obj.ToSingleElementList().ToArray());
            var query = new Qb().Append($"DELETE FROM {obj.GetType().Name} WHERE {ridname}=@rid", obj.RID);
            retv = Execute(transaction, query) > 0;
            return retv;
        }

        public bool Delete<T>(BDadosTransaction transaction, IEnumerable<T> obj) where T : IDataObject, new() {
            transaction.Step();

            bool retv = false;

            var ridcol = FiTechBDadosExtensions.RidColumnOf[typeof(T)];
            string ridname = "RID";
            if (ridcol != null) {
                ridname = ridcol;
            }
            OnObjectsDeleted(typeof(T), obj.Select(t => t as IDataObject).ToArray());
            var query = Qb.Fmt($"DELETE FROM {typeof(T).Name} WHERE ") + Qb.In(ridname, obj.ToList(), o => o.RID);
            retv = Execute(transaction, query) > 0;
            return retv;
        }

        public bool SaveItem(BDadosTransaction transaction, IDataObject input) {
            transaction.Step();

            if (input == null) {
                throw new BDadosException("Error saving item", transaction.FrameHistory, new List<IDataObject>(), new ArgumentNullException("Input to SaveItem must be not-null"));
            }

            bool retv = false;

            int rs = -33;

            var id = GetIdColumn(input.GetType());
            var rid = GetRidColumn(input.GetType());

            if (!input.IsReceivedFromSync) {
                input.UpdatedTime = Fi.Tech.GetUtcTime();
            }

            transaction?.Benchmarker.Mark($"SaveItem<{input.GetType().Name}> check persistence");
            input.IsPersisted = false;
            var persistedMap = Query(transaction, Plugin.QueryGenerator.QueryIds(input.ToSingleElementList()));
            foreach (DataRow a in persistedMap.Rows) {
                if (input.RID == (string) Convert.ChangeType(a[1], typeof(String))) {
                    input.IsPersisted = true;
                }
            }
            transaction?.Benchmarker.Mark($"SaveItem<{input.GetType().Name}> isPersisted? {input.IsPersisted}");

            try {
                if (input.IsPersisted) {
                    transaction?.Benchmarker.Mark($"SaveItem<{input.GetType().Name}> generating UPDATE query");
                    var query = Plugin.QueryGenerator.GenerateUpdateQuery(input);
                    transaction?.Benchmarker.Mark($"SaveItem<{input.GetType().Name}> executing query");
                    rs = Execute(transaction, query);
                    transaction?.Benchmarker.Mark($"SaveItem<{input.GetType().Name}> query executed OK");
                    retv = true;
                    transaction.NotifyChange(input.ToSingleElementList().ToArray());
                    return retv;
                } else {
                    transaction?.Benchmarker.Mark($"SaveItem<{input.GetType().Name}> generating INSERT query");
                    var query = Plugin.QueryGenerator.GenerateInsertQuery(input);
                    transaction?.Benchmarker.Mark($"SaveItem<{input.GetType().Name}> executing query");
                    retv = true;
                    rs = Execute(transaction, query);
                    transaction?.Benchmarker.Mark($"SaveItem<{input.GetType().Name}> query executed OK");
                }
            } catch (Exception x) {
                if(OnFailedSave!=null) {
                    Fi.Tech.FireAndForget(async () => {
                        await Task.Yield();
                        OnFailedSave?.Invoke(input.GetType(), new List<IDataObject> { input }.ToArray(), x);
                    }, async (xe) => {
                        await Task.Yield();
                        Fi.Tech.Throw(xe);
                    });
                }
                throw x;
            }
            if (rs == 0) {
                WriteLog("** Something went SERIOUSLY NUTS in SaveItem<T> **");
            }

            transaction.OnTransactionEnding += () => {
                if (retv && !input.IsPersisted) {
                    if (input.Id <= 0) {
                        long retvId = 0;
                        var method = typeof(IQueryGenerator).GetMethods().FirstOrDefault(x=> x.Name == "QueryIds");
                        var genericMethod = method.MakeGenericMethod(input.GetType());
                        var queryIds = Query(transaction, (IQueryBuilder) genericMethod.Invoke(this.QueryGenerator, new object[] { input.ToSingleElementListRefl() }));
                        foreach (DataRow dr in queryIds.Rows) {
                            var psave = input;
                            if (psave != null) {
                                psave.Id = Int64.Parse(dr[0] as String);
                            }
                        }
                    }

                    var newHash = input.SpFthComputeDataFieldsHash();
                    if (input.PersistedHash != newHash) {
                        input.PersistedHash = newHash;
                        input.AlteredBy = IDataObjectExtensions.localInstanceId;
                    }
                    transaction.NotifyChange(input.ToSingleElementList().ToArray());
                    retv = true;
                }

                if (OnSuccessfulSave != null) {
                    Fi.Tech.FireAndForget(async () => {
                        await Task.Yield();
                        OnSuccessfulSave?.Invoke(input.GetType(), new List<IDataObject> { input }.ToArray());
                    }, async (xe) => {
                        await Task.Yield();
                        Fi.Tech.Throw(xe);
                    });
                }
            };
            

            return rs > 0;
        }

        static SelfInitializerDictionary<Type, PrefixMaker> CacheAutoPrefixer = new SelfInitializerDictionary<Type, PrefixMaker>(
            type => {
                return new PrefixMaker();
            }
        );
        static SelfInitializerDictionary<Type, PrefixMaker> CacheAutoPrefixerLinear = new SelfInitializerDictionary<Type, PrefixMaker>(
            type => {
                return new PrefixMaker();
            }
        );

        static SelfInitializerDictionary<Type, JoinDefinition> CacheAutoJoinLinear = new SelfInitializerDictionary<Type, JoinDefinition>(
            type => {
                var retv = CacheAutomaticJoinBuilderLinear[type].GetJoin();
                var _buildParameters = new BuildParametersHelper(retv);
                MakeBuildAggregations(_buildParameters, type, "root", type.Name, String.Empty, CacheAutoPrefixerLinear[type], true);

                return retv;
            }
        );
        static SelfInitializerDictionary<Type, JoinDefinition> CacheAutoJoin = new SelfInitializerDictionary<Type, JoinDefinition>(
            type => {
                var retv = CacheAutomaticJoinBuilder[type].GetJoin();
                var _buildParameters = new BuildParametersHelper(retv);
                MakeBuildAggregations(_buildParameters, type, "root", type.Name, String.Empty, CacheAutoPrefixer[type], false);

                return retv;
            }
        );

        static SelfInitializerDictionary<Type, IJoinBuilder> CacheAutomaticJoinBuilder = new SelfInitializerDictionary<Type, IJoinBuilder>(
            type => {
                var prefixer = CacheAutoPrefixer[type];
                return MakeJoin(
                    (query) => {
                        // Starting with T itself
                        var jh = query.AggregateRoot(type, prefixer.GetAliasFor("root", type.Name, String.Empty)).As(prefixer.GetAliasFor("root", type.Name, String.Empty));
                        jh.OnlyFields(
                            ReflectionTool.FieldsWithAttribute<FieldAttribute>(type)
                            .Select(a => a.Name)
                        );
                        MakeQueryAggregations(ref query, type, "root", type.Name, String.Empty, prefixer, false);
                    });
            }
        );
        static SelfInitializerDictionary<Type, IJoinBuilder> CacheAutomaticJoinBuilderLinear = new SelfInitializerDictionary<Type, IJoinBuilder>(
            type => {
                var prefixer = CacheAutoPrefixerLinear[type];
                return MakeJoin(
                    (query) => {
                        // Starting with T itself
                        var jh = query.AggregateRoot(type, prefixer.GetAliasFor("root", type.Name, String.Empty)).As(prefixer.GetAliasFor("root", type.Name, String.Empty));
                        jh.OnlyFields(
                            ReflectionTool.FieldsWithAttribute<FieldAttribute>(type)
                            .Select(a => a.Name)
                        );
                        MakeQueryAggregations(ref query, type, "root", type.Name, String.Empty, prefixer, true);
                    });
            }
        );

        public List<T> AggregateLoad<T>
            (BDadosTransaction transaction,
            LoadAllArgs<T> args = null) where T : IDataObject, new() {
            transaction.Step();
            args = args ?? new LoadAllArgs<T>();
            var limit = args.RowLimit;
            if (limit < 0) {
                limit = DefaultQueryLimit;
            }
            transaction?.Benchmarker?.Mark($"Begin AggregateLoad<{typeof(T).Name}>");
            var Members = ReflectionTool.FieldsAndPropertiesOf(typeof(T));
            var prefixer = args.Linear ? CacheAutoPrefixerLinear[typeof(T)] : CacheAutoPrefixer[typeof(T)];
            bool hasAnyAggregations = false;
            transaction?.Benchmarker?.Mark("Check if model is Aggregate");
            foreach (var a in Members) {
                hasAnyAggregations =
                    a.GetCustomAttribute<AggregateFieldAttribute>() != null ||
                    a.GetCustomAttribute<AggregateFarFieldAttribute>() != null ||
                    a.GetCustomAttribute<AggregateObjectAttribute>() != null ||
                    a.GetCustomAttribute<AggregateListAttribute>() != null;
                if (hasAnyAggregations)
                    break;
            }

            WriteLog($"Running Aggregate Load All for {typeof(T).Name}? {hasAnyAggregations}. Linear? {args.Linear}");
            // CLUMSY
            if (hasAnyAggregations) {

                transaction?.Benchmarker?.Mark("Construct Join Definition");

                transaction?.Benchmarker?.Mark("Resolve ordering Member");
                var om = GetOrderingMember(args.OrderingMember);
                transaction?.Benchmarker?.Mark("--");

                using (var command = transaction?.CreateCommand()) {
                    var join = args.Linear ? CacheAutoJoinLinear[typeof(T)] : CacheAutoJoin[typeof(T)];

                    var builtConditions = (args.Conditions == null ? Qb.Fmt("TRUE") : new ConditionParser(prefixer).ParseExpression(args.Conditions));
                    var builtConditionsRoot = (args.Conditions == null ? Qb.Fmt("TRUE") : new ConditionParser(prefixer).ParseExpression(args.Conditions, false));

                    var query = Plugin.QueryGenerator.GenerateJoinQuery(join, builtConditions, args.RowSkip, limit, om, args.OrderingType, builtConditionsRoot);
                    try {
                        transaction?.Benchmarker?.Mark($"Generate Join Query");
                        //var _buildParameters = Linear ? CacheBuildParamsLinear[typeof(T)] : CacheBuildParams[typeof(T)];
                        query.ApplyToCommand(command, Plugin.ProcessParameterValue);
                        transaction?.Benchmarker?.Mark($"Start build AggregateListDirect<{typeof(T).Name}> ({query.Id})");
                        var retv = BuildAggregateListDirect<T>(transaction, command, join, 0, args.ContextObject);
                        transaction?.Benchmarker?.Mark($"Finished building the resultAggregateListDirect<{typeof(T).Name}> ({query.Id})");
                        return retv;
                    } catch(Exception x) {
                        throw new BDadosException($"Error executing AggregateLoad query; Linear? {args.Linear}; Query Text: {query.GetCommandText()}", x);
                    }
                }

            } else {
                WriteLog(args.Conditions?.ToString());
                return Fetch<T>(transaction, args).ToList();
            }
        }

        public T LoadFirstOrDefault<T>(BDadosTransaction transaction, LoadAllArgs<T> args = null) where T : IDataObject, new() {
            transaction.Step();
            return LoadAll<T>(transaction, args).FirstOrDefault();
        }

        public IEnumerable<T> Fetch<T>(BDadosTransaction transaction, LoadAllArgs<T> args = null) where T : IDataObject, new() {
            transaction.Step();
            var cndParse = new ConditionParser();
            var cnd = cndParse.ParseExpression(args?.Conditions);
            return Fetch<T>(transaction, cnd, args.RowSkip, args.RowLimit, args.OrderingMember, args.OrderingType, args.ContextObject);
        }

        public int DefaultQueryLimit { get; set; } = 50;

        public IEnumerable<T> Fetch<T>(BDadosTransaction transaction, IQueryBuilder conditions, int? skip, int? limit, Expression<Func<T, object>> orderingMember = null, OrderingType ordering = OrderingType.Asc, object transferObject = null) where T : IDataObject, new() {
            transaction.Step();
            if (limit < 0) {
                limit = DefaultQueryLimit;
            }
            if (conditions == null) {
                conditions = Qb.Fmt("TRUE");
            }

            if (transaction == null) {
                throw new BDadosException("Fatal inconsistency error: Fetch<T> Expects a functional initialized ConnectionInfo object.");
            }

            transaction.Benchmarker?.Mark("--");

            transaction.Benchmarker?.Mark("Data Load ---");
            MemberInfo ordMember = GetOrderingMember<T>(orderingMember);
            transaction.Benchmarker?.Mark($"Generate SELECT<{typeof(T).Name}>");
            var query = Plugin.QueryGenerator.GenerateSelect<T>(conditions, skip, limit, ordMember, ordering);
            transaction.Benchmarker?.Mark($"Execute SELECT<{typeof(T).Name}>");
            transaction.Step();
            if (query == null || query.GetCommandText() == null) {
                return new T[0];
            }
            DateTime start = DateTime.UtcNow;
            using (var command = transaction.CreateCommand()) {
                command.CommandTimeout = Plugin.CommandTimeout;
                VerboseLogQueryParameterization(transaction, query);
                query.ApplyToCommand(command, Plugin.ProcessParameterValue);
                IDataReader reader;
                try {

                    transaction?.Benchmarker?.Mark($"[{accessId}] Wait for locked region");
                    lock (transaction) {
                        lock (command) {
                            transaction?.Benchmarker?.Mark($"[{accessId}] Execute Query <{query.Id}>");
                            reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
                        }
                    }
                    transaction?.Benchmarker?.Mark($"[{accessId}] Query <{query.Id}> executed OK");

                } catch (Exception x) {
                    WriteLog($"[{accessId}] -------- Error: {x.Message} ([{DateTime.UtcNow.Subtract(start).TotalMilliseconds} ms]");
                    WriteLog(x.Message);
                    WriteLog(x.StackTrace);
                    throw x;
                } finally {
                    WriteLog("------------------------------------");
                }
                transaction?.Benchmarker?.Mark($"[{accessId}] Reader executed OK <{query.Id}>");
                WorkQueuer wq = new WorkQueuer("Fetch Builder", Math.Max(1, Environment.ProcessorCount - 2), true);
                using (reader) {
                    var cols = new string[reader.FieldCount];
                    for (int i = 0; i < cols.Length; i++)
                        cols[i] = reader.GetName(i);
                    transaction?.Benchmarker?.Mark($"[{accessId}] Build retv List<{typeof(T).Name}> ({query.Id})");

                    var existingKeys = new MemberInfo[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++) {
                        var name = reader.GetName(i);
                        if (name != null) {
                            var m = ReflectionTool.GetMember(typeof(T), name);
                            if(m != null) {
                                existingKeys[i] = m;
                            }
                        }
                    }
                    int c = 0;
                    var retv = new List<T>();
                    while (reader.Read()) {

                        object[] values = new object[reader.FieldCount];
                        for (int i = 0; i < reader.FieldCount; i++) {
                            values[i] = reader.GetValue(i);
                        }
                        T obj = new T();
                        for (int i = 0; i < existingKeys.Length; i++) {
                            try {
                                if(existingKeys[i] != null) {
                                    ReflectionTool.SetMemberValue(existingKeys[i], obj, Fi.Tech.ProperMapValue(values[i]));
                                }
                            } catch (Exception x) {
                                throw x;
                            }
                        }
                        RunAfterLoad(obj, false, transferObject ?? transaction?.ContextTransferObject);
                        retv.Add(obj);
                        c++;
                    }
                    double elaps = (DateTime.UtcNow - start).TotalMilliseconds;
                    WriteLog($"[{accessId}] -------- <{query.Id}> Fetch [OK] ({c} results) [{elaps} ms]");
                    return retv;
                }
            }
        }

        public void QueryReader(BDadosTransaction transaction, IQueryBuilder query, Action<IDataReader> actionRead) {
            QueryReader<int>(transaction, query, (reader) => { actionRead(reader); return 0; });
        }
        public T QueryReader<T>(BDadosTransaction transaction, IQueryBuilder query, Func<IDataReader, T> actionRead) {
            transaction.Step();
            if (query == null || query.GetCommandText() == null) {
                return default(T);
            }
            DateTime Inicio = DateTime.Now;
            DataTable retv = new DataTable();
            using (var command = transaction.CreateCommand()) {
                VerboseLogQueryParameterization(transaction, query);
                query.ApplyToCommand(command, Plugin.ProcessParameterValue);
                // --
                transaction?.Benchmarker?.Mark($"[{accessId}] Build Dataset");
                using (var reader = command.ExecuteReader()) {
                    return actionRead(reader);
                }
            }
        }

        public DataTable Query(BDadosTransaction transaction, IQueryBuilder query) {
            transaction.Step();
            if (query == null || query.GetCommandText() == null) {
                return new DataTable();
            }
            DateTime Inicio = DateTime.Now;
            DataTable retv = new DataTable();
            using (var command = transaction.CreateCommand()) {
                VerboseLogQueryParameterization(transaction, query);
                query.ApplyToCommand(command, Plugin.ProcessParameterValue);
                // --
                transaction?.Benchmarker?.Mark($"[{accessId}] Build Dataset");
                DataSet ds;
                lock (transaction) {
                    ds = GetDataSet(command);
                }
                var elaps = transaction?.Benchmarker?.Mark($"[{accessId}] --");

                try {
                    int resultados = 0;
                    if (ds.Tables.Count < 1) {
                        throw new BDadosException("Database did not return any table.");
                    }
                    resultados = ds.Tables[0].Rows.Count;
                    transaction?.Benchmarker?.Mark($"[{accessId}] -------- Queried [OK] ({resultados} results) [{elaps} ms]");
                    return ds.Tables[0];
                } catch (Exception x) {
                    transaction?.Benchmarker?.Mark($"[{accessId}] -------- Error: {x.Message} ([{DateTime.Now.Subtract(Inicio).TotalMilliseconds} ms]");
                    WriteLog(x.Message);
                    WriteLog(x.StackTrace);
                    var ex = new BDadosException("Error executing Query", x);
                    ex.Data["query"] = query;
                    throw ex;
                } finally {
                    WriteLog("------------------------------------");
                }
            }
        }

        public int Execute(BDadosTransaction transaction, IQueryBuilder query) {
            transaction.Step();
            if (query == null)
                return 0;
            int result = -1;
            transaction.Benchmarker?.Mark($"[{accessId}] Prepare statement");
            transaction.Benchmarker?.Mark("--");
            WriteLog($"[{accessId}] -- Execute Statement <{query.Id}> [{Plugin.CommandTimeout}s timeout]");
            using (var command = transaction.CreateCommand()) {
                try {
                    VerboseLogQueryParameterization(transaction, query);
                    query.ApplyToCommand(command, Plugin.ProcessParameterValue);
                    transaction.Benchmarker?.Mark($"[{accessId}] Execute");
                    lock (transaction) {
                        result = command.ExecuteNonQuery();
                    }
                    var elaps = transaction.Benchmarker?.Mark("--");
                    WriteLog($"[{accessId}] --------- Executed [OK] ({result} lines affected) [{elaps} ms]");
                } catch (Exception x) {
                    WriteLog($"[{accessId}] -------- Error: {x.Message} ([{transaction.Benchmarker?.Mark("Error")} ms]");
                    WriteLog(x.Message);
                    WriteLog(x.StackTrace);
                    WriteLog($"BDados Execute: {x.Message}");
                    throw x;
                } finally {
                    WriteLog("------------------------------------");
                }
            }
            return result;
        }

        #endregion

    }
}