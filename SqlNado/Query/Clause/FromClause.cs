using System.Linq;
using System.Text;

namespace SqlNado.Query.Clause
{
    public class FromClause<T> : Clause
    {
        public override string Name => "from";

        public FromClause()
        {

        }

        public override string ToString()
        {
            StringBuilder query = new StringBuilder();

            query.Append(this.Name + " ");

            query.AppendLine(this.TableNameAsString());

            return query.ToString();
        }

        private string TableNameAsString()
        {
            var attributes = typeof(T).GetCustomAttributes(false);

            var tableAttribute = (attributes.FirstOrDefault(attr => attr is SQLiteTableAttribute));

            if ((tableAttribute is SQLiteTableAttribute table) && (table.Name != null))
            {
                return (table.Name);
            }
            return typeof(T).Name;
        }
    }
}
