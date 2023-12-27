using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace SqlNado.Query.Filter
{
    public class DbQueryFilter : ExpressionVisitor
    {
        private Stack<string> _fieldNames = new Stack<string>();
        private readonly Dictionary<ExpressionType, string> _logicalOperators;

        private readonly Dictionary<Type, Func<object, string>> _typeConverters;

        private readonly StringBuilder _queryStringBuilder;

        public DbQueryFilter()
        {
            _queryStringBuilder = new StringBuilder();

            _logicalOperators = new Dictionary<ExpressionType, string>
            {
                [ExpressionType.AndAlso] = "and",
                [ExpressionType.OrElse] = "or",
                [ExpressionType.LessThan] = "<",
                [ExpressionType.LessThanOrEqual] = "<=",
                [ExpressionType.LessThan] = "<",
                [ExpressionType.Equal] = "=",
                [ExpressionType.NotEqual] = "<>",
                [ExpressionType.GreaterThan] = ">",
                [ExpressionType.GreaterThanOrEqual] = ">="
            };

            _typeConverters = new Dictionary<Type, Func<object, string>>
            {
                [typeof(string)] = x => $"'{x}'",
                [typeof(DateTime)] = x => $"datetime'{((DateTime)x).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}'",
                [typeof(bool)] = x => x.ToString().ToLower()
            };

        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var argument = node.Arguments.FirstOrDefault();

            var value = argument?.ToString();
            string name = string.Empty;

            if (node.Object.NodeType == ExpressionType.MemberAccess)
            {
                var memberInfo = ((MemberExpression)(node.Object)).Member;
                var columnMap = memberInfo.GetCustomAttribute<SQLiteColumnAttribute>();
                if (columnMap?.Ignore == false)
                {
                    name = columnMap.Name;
                }
                else
                {
                    name = memberInfo.Name;
                }
            }

            switch (node.Method.Name.ToLower())
            {
                case "startswith":
                    _queryStringBuilder.Append($"( {name} like N'{value.Replace("\"", "")}%' )");
                    break;
                case "endswith":
                    _queryStringBuilder.Append($"( {name} like N'%{value.Replace("\"", "")}' )");
                    break;
                case "contains":
                    _queryStringBuilder.Append($"( {name} like N'%{value.Replace("\"", "")}%' )");
                    break;
                case "tolower":
                    _queryStringBuilder.Append($"{node.Object.ToString().ToLower()}");
                    break;
                case "toupper":
                    _queryStringBuilder.Append($"{node.Object.ToString().ToUpper()}");
                    break;
                default:
                    break;
            }

            return node;
        }


        public string AsQuery(LambdaExpression predicate)
        {
            Visit(predicate.Body);

            var query = _queryStringBuilder.ToString();

            _queryStringBuilder.Clear();

            return query;
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            _queryStringBuilder.Append("(");

            Visit(node.Left);

            _queryStringBuilder.Append($" {_logicalOperators[node.NodeType]} ");

            Visit(node.Right);

            _queryStringBuilder.Append(")");

            return node;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression.NodeType == ExpressionType.Constant ||
                node.Expression.NodeType == ExpressionType.MemberAccess)
            {
                _fieldNames.Push(node.Member.Name);
                 Visit(node.Expression);
            }
            else
            {
                var columnMap = node.Member.GetCustomAttribute<SQLiteColumnAttribute>();
                if (columnMap?.Ignore == false)
                {
                    _queryStringBuilder.Append(columnMap.Name);
                }
                else
                {
                    _queryStringBuilder.Append(node.Member.Name);
                }
            }

            return node;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            _queryStringBuilder.Append(GetValue(node.Value));

            return node;
        }

        private string GetValue(object input)
        {
            var type = input.GetType();
            //if it is not simple value
            if (type.IsClass && type != typeof(string))
            {
                var fieldName = _fieldNames.Pop();

                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                var fieldInfo = fields.FirstOrDefault(field => field.Name == fieldName);
                //proper order of selected names provided by means of Stack structure
                //var fieldInfo = type.GetField(fieldName);
                object value;
                if (fieldInfo != null)
                    //get instance of order    
                    value = fieldInfo.GetValue(input);
                else
                    //get value of "Customer" property on order
                    value = type.GetProperty(fieldName).GetValue(input);
                return GetValue(value);
            }
            else
            {
                //our predefined _typeConverters
                if (_typeConverters.ContainsKey(type))
                    return _typeConverters[type](input);
                else
                    //rest types
                    return input.ToString();
            }
        }
    }

}
