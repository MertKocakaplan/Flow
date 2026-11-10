namespace FlowTests {
    public static partial class Unit {
        static partial void RunLater() {
            Stage3Unit.Run();
            QueueTests.Run();
            Stage5Unit.Run();
            Stage6Unit.Run();
            Stage7Unit.Run();
            ContractTests.Run();
            FixUnit.Run();
            NextUnit.Run();
        }
    }
}
