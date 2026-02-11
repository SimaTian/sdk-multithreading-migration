namespace Microsoft.Build.Framework
{
    public interface IMultiThreadableTask : ITask
    {
        TaskEnvironment TaskEnvironment { get; set; }
    }
}
