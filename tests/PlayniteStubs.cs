// Minimal stand-ins so the production Hydra-layer sources compile outside Playnite.
namespace Playnite.SDK
{
    public interface ILogger
    {
        void Info(string message);
        void Info(System.Exception exception, string message);
        void Debug(string message);
        void Debug(System.Exception exception, string message);
        void Warn(string message);
        void Warn(System.Exception exception, string message);
        void Error(string message);
        void Error(System.Exception exception, string message);
    }

    public static class LogManager
    {
        private sealed class NullLogger : ILogger
        {
            public void Info(string message) { }
            public void Info(System.Exception e, string message) { }
            public void Debug(string message) { }
            public void Debug(System.Exception e, string message) { }
            public void Warn(string message) { }
            public void Warn(System.Exception e, string message) { }
            public void Error(string message) { }
            public void Error(System.Exception e, string message) { }
        }

        public static ILogger GetLogger(string name) => new NullLogger();
    }
}
