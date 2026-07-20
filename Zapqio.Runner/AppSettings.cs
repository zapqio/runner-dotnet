namespace Zapqio.Runner
{
    public class AppSettings
    {
        public LoggerClass Logger { get; set; }
        public string Token { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }


        public class LoggerClass
        {
            public string LogLevel { get; set; }
            public string PathDirectory { get; set; }
        }
    }

}
