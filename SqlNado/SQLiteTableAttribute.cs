using System;
using System.Linq.Expressions;

namespace SqlNado
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
    public sealed class SQLiteTableAttribute : Attribute
    {
        public string? Name { get; set; }
        public string? Schema { get; set; } // unused in SqlNado's SQLite
        public string? Module { get; set; } // virtual table
        public string? ModuleArguments { get; set; } // virtual table

        // note every WITHOUT ROWID table must have a PRIMARY KEY
        public bool WithoutRowId { get; set; }

        public override string? ToString() => Name;

        public static string ColumnName<T>(Expression<Func<T>> e)
        {
            object instance;

            if (e.Body is MemberExpression member)
            {
                if (member.Expression is ConstantExpression constant)
                {
                    var attributes = member.Member.GetCustomAttributes(typeof(SQLiteTableAttribute), true);

                    if (attributes.Length > 0)
                    {
                        var column = (SQLiteTableAttribute)attributes[0];
                        return (column.Name);
                    }
                }
            }
            return (string.Empty);
        }

    }
}
