using Flow;

namespace FlowTests {
    public static partial class LiveTests {
        static partial void RunMore(QueueService q, string root) {
            var more = NewQueue(root, "data5");
            Stage5Live(more, root);
            more.Shutdown();
            RunLater(root);
        }
        static partial void RunLater(string root);
    }
}
