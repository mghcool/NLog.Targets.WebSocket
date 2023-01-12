using NLog;

namespace NLogWebSocketSample
{
    internal class Program
    {
        static readonly Logger logger = LogManager.GetCurrentClassLogger();

        static void Main(string[] args)
        {
            Random random = new Random();
            while (true)
            {
                logger.Info($"随机数：{random.Next()}");
                Thread.Sleep(1000);
            }
        }
    }
}