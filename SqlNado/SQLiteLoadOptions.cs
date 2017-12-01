﻿using System;
using System.Text;

namespace SqlNado
{
    public class SQLiteLoadOptions
    {
        public SQLiteLoadOptions(SQLiteDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            Database = database;
        }

        public virtual SQLiteDatabase Database { get; }
        public virtual bool ObjectEventsDisabled { get; set; }
        public virtual bool ObjectChangeEventsDisabled { get; set; }
        public virtual int MaximumRows { get; set; }
        public virtual Func<Type, SQLiteStatement, SQLiteLoadOptions, object> GetInstanceFunc { get; set; }
        public virtual Func<SQLiteError, SQLiteOnErrorAction> ErrorHandler { get; set; }

        public virtual bool TryChangeType(object input, Type conversionType, out object value) => Database.TryChangeType(input, conversionType, out value);

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("ObjectEventsDisabled=" + ObjectEventsDisabled);
            sb.AppendLine("ObjectChangeEventsDisabled=" + ObjectChangeEventsDisabled);
            sb.AppendLine("MaximumRows=" + MaximumRows);
            return sb.ToString();
        }
    }
}
