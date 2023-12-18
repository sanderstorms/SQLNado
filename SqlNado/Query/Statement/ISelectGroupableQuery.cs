using System;
using System.Linq.Expressions;

namespace SqlNado.Query.Statement
{
    public interface ISelectGroupableQuery<T> : ISelectOrderableQuery<T>
    {
        ISelectOrderableQuery<T> GroupBy(Expression<Func<T, object>> selector);
    }
}
