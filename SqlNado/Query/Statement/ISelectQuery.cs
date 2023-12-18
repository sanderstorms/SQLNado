namespace SqlNado.Query.Statement
{


    public interface ISelectQuery<T>
    {
        Statement Top(int number);
        Statement Top(int number, bool percent);
        ISelectQuery<T> Distinct();
    }
}
