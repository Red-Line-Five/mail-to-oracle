using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace OwaToOracle
{
    /// <summary>
    /// Oracle access WITHOUT a compile-time reference: Oracle.ManagedDataAccess.dll is loaded at run time
    /// from the folder of the .exe and used through the standard System.Data interfaces.
    /// </summary>
    public static class OracleDb
    {
        private static Type _connType;

        private static Type ConnType()
        {
            if (_connType != null) return _connType;
            string dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Oracle.ManagedDataAccess.dll");
            if (!File.Exists(dll))
                throw new Exception("Oracle.ManagedDataAccess.dll was not found next to the program (" +
                                    AppDomain.CurrentDomain.BaseDirectory + "). See README step 2.");
            Assembly asm = Assembly.LoadFrom(dll);
            _connType = asm.GetType("Oracle.ManagedDataAccess.Client.OracleConnection", true);
            return _connType;
        }

        public static IDbConnection Open(string connectionString)
        {
            var conn = (IDbConnection)Activator.CreateInstance(ConnType(), new object[] { connectionString });
            conn.Open();
            return conn;
        }

        /// <summary>Only letters, digits, _ $ # and dots - stops SQL injection through the settings.</summary>
        public static string Id(string name)
        {
            name = (name ?? "").Trim();
            if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_$#\.]*$"))
                throw new Exception("Invalid table/column name: '" + name + "'");
            return name;
        }

        public static string InsertSql(Settings s)
        {
            return "INSERT INTO " + Id(s.Table) + " (" + Id(s.ColName1) + ", " + Id(s.ColName2) + ", " +
                   Id(s.ColUser) + ", " + Id(s.ColDate) + ") VALUES (:p1, :p2, :p3, SYSDATE)";
        }

        private static void AddParam(IDbCommand cmd, string name, string value)
        {
            IDbDataParameter p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value == null ? (object)DBNull.Value : value;
            cmd.Parameters.Add(p);
        }

        /// <summary>INSERT one row: name1 = A value, name2 = D value, add_user, add_date = SYSDATE. Auto-commit.</summary>
        public static void Insert(IDbConnection conn, string sql, string a, string d, string who)
        {
            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                PropertyInfo bind = cmd.GetType().GetProperty("BindByName");
                if (bind != null) bind.SetValue(cmd, true, null);
                AddParam(cmd, "p1", a);
                AddParam(cmd, "p2", d);
                AddParam(cmd, "p3", who);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>UPDATE that marks the still-active row(s) with this A/D pair as deleted.</summary>
        public static string UpdateSql(Settings s, bool hasA, bool hasD)
        {
            var sb = new StringBuilder();
            sb.Append("UPDATE ").Append(Id(s.Table))
              .Append(" SET ").Append(Id(s.ColDelUser)).Append(" = :pu, ")
              .Append(Id(s.ColDelDate)).Append(" = SYSDATE WHERE ");
            sb.Append(Id(s.ColName1)).Append(hasA ? " = :pa" : " IS NULL");
            sb.Append(" AND ");
            sb.Append(Id(s.ColName2)).Append(hasD ? " = :pd" : " IS NULL");
            sb.Append(" AND ").Append(Id(s.ColDelDate)).Append(" IS NULL");
            return sb.ToString();
        }

        /// <summary>status:add  ->  INSERT a new row (name1 = A value, name2 = D value).</summary>
        public static void Add(IDbConnection conn, Settings s, string a, string d, string who)
        {
            Insert(conn, InsertSql(s), a, d, who);
        }

        /// <summary>
        /// status:delete  ->  UPDATE the active row(s) with this A/D pair: set delete_user + delete_date.
        /// Returns the number of rows updated (0 = no active row found).
        /// </summary>
        public static int Delete(IDbConnection conn, Settings s, string a, string d, string who)
        {
            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = UpdateSql(s, a != null, d != null);
                PropertyInfo bind = cmd.GetType().GetProperty("BindByName");
                if (bind != null) bind.SetValue(cmd, true, null);
                AddParam(cmd, "pu", who);
                if (a != null) AddParam(cmd, "pa", a);
                if (d != null) AddParam(cmd, "pd", d);
                return cmd.ExecuteNonQuery();
            }
        }

        // ---------------------------------------------------------------- status:search
        private static string LikeValue(string v)
        {
            return "%" + v.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";   // user text is literal, not a pattern
        }

        public static string[] SearchHeader(Settings s)
        {
            return new[] { Id(s.ColName1), Id(s.ColName2), Id(s.ColUser), Id(s.ColDate), Id(s.ColDelUser), Id(s.ColDelDate) };
        }

        public static string SearchSql(Settings s, bool hasA, bool hasD)
        {
            var w = new List<string>();
            if (hasA) w.Add("UPPER(" + Id(s.ColName1) + ") LIKE UPPER(:pa) ESCAPE '\\'");
            if (hasD) w.Add("UPPER(" + Id(s.ColName2) + ") LIKE UPPER(:pd) ESCAPE '\\'");
            return "SELECT * FROM (SELECT " + string.Join(", ", SearchHeader(s)) + " FROM " + Id(s.Table) +
                   " WHERE " + string.Join(" AND ", w.ToArray()) +
                   " ORDER BY " + Id(s.ColDate) + " DESC) WHERE ROWNUM <= :pmax";
        }

        /// <summary>name1 LIKE %a%  [AND  name2 LIKE %d%]  (case-insensitive). Newest rows first, at most 'max' rows.</summary>
        public static List<string[]> Search(IDbConnection conn, Settings s, string a, string d, int max, out bool more)
        {
            var rows = new List<string[]>();
            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = SearchSql(s, a != null, d != null);
                PropertyInfo bind = cmd.GetType().GetProperty("BindByName");
                if (bind != null) bind.SetValue(cmd, true, null);
                if (a != null) AddParam(cmd, "pa", LikeValue(a));
                if (d != null) AddParam(cmd, "pd", LikeValue(d));
                IDbDataParameter pm = cmd.CreateParameter();
                pm.ParameterName = "pmax";
                pm.Value = max + 1;                                  // one extra row tells us "there is more"
                cmd.Parameters.Add(pm);

                using (IDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var line = new string[r.FieldCount];
                        for (int i = 0; i < r.FieldCount; i++)
                        {
                            object v = r.GetValue(i);
                            if (v == null || v is DBNull) line[i] = "";
                            else if (v is DateTime) line[i] = ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss");
                            else line[i] = Convert.ToString(v);
                        }
                        rows.Add(line);
                    }
                }
            }
            more = rows.Count > max;
            if (more) rows.RemoveRange(max, rows.Count - max);
            return rows;
        }

        /// <summary>Connects and checks that the table and the columns exist.</summary>
        public static void Check(Settings s)
        {
            using (IDbConnection conn = Open(s.OracleConn))
            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + Id(s.ColName1) + ", " + Id(s.ColName2) + ", " + Id(s.ColUser) + ", " +
                                  Id(s.ColDate) + ", " + Id(s.ColDelUser) + ", " + Id(s.ColDelDate) + " FROM " + Id(s.Table) + " WHERE 1 = 0";
                using (IDataReader r = cmd.ExecuteReader()) { }
            }
        }
    }
}
