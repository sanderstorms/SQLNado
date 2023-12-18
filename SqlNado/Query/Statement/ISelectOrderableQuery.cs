using System;
using System.Linq.Expressions;

namespace SqlNado.Query.Statement
{
    public interface ISelectOrderableQuery<T> : ISelectableQuery<T>, ISelectQuery<T>
    {
        ISelectQuery<T> OrderBy(Expression<Func<T, object>> selector);
    }
}
