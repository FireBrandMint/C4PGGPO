using System;
using System.Text;

public class TestProgram
{
    static int CurrMoment = 0;

    static C4PGGPO<TestInput> GGPOInst = null;

    public static void Main (string[] args)
    {
        Test();

        Console.WriteLine();

        Console.WriteLine("Hello! Welcome to C4GGPO tester!");
        Console.WriteLine("To start please specify the amount of save states c4pggpo is allowed to use.");

        int saveAmount = 0;
        confirm:;
        Console.Write("> ");

        if(!int.TryParse(Console.ReadLine(), out saveAmount)) goto confirm;

        GGPOInst = new C4PGGPO<TestInput>(0, 4, 4, saveAmount,
        new C4PGGPO<TestInput>.CSGGPOEvents<TestInput>(Save, Load, Tick, TestInput.CreateDefault, GetDefaultWorldState, PrintInConsole)
        );

        GGPOInst.Tick();

        bool ended = false;

        while(!ended)
        {
            Console.Write("\n> ");

            string command = Console.ReadLine();

            if(command.Length == 0) Console.WriteLine("TYPE SOMETHING!");

            string[] arguments = command.Split(' ');

            switch(arguments[0])
            {
                case "help":
                Console.WriteLine("next - Advance tick.");
                Console.WriteLine("skip <number> - Skips a number amount of ticks");
                Console.WriteLine("addinput <id> <ticknow> <inputNumber> - Adds input of player specified to tick specified.");
                Console.WriteLine();
                break;

                case "next":
                var error0 = GGPOInst.Tick();
                if(error0.error != RollbackErrors.NONE) Console.WriteLine("ERROR: " + error0.error.ToString() + ' ' + error0.id.ToString());
                break;

                case "skip":
                int number;
                if(arguments.Length == 2 && int.TryParse(arguments[1], out number))
                {
                    for(int i = 0; i < number; ++i)
                    {
                        var error1 = GGPOInst.Tick();
                        if(error1.error != RollbackErrors.NONE)
                        {
                            Console.WriteLine("ERROR: " + error1.error.ToString() + ' ' + error1.id.ToString());
                            break;
                        }
                    }
                }
                else Console.WriteLine("Command typed wrongly.");
                break;

                case "addinput":
                int id;
                int ticknow;
                int inputNumber;
                if(arguments.Length == 4 && int.TryParse(arguments[1], out id) && int.TryParse(arguments[2], out ticknow) && int.TryParse(arguments[3], out inputNumber))
                {
                    var error2 = GGPOInst.InsertInput(ticknow, id, new TestInput(inputNumber));

                    if(error2 != RollbackErrors.NONE) Console.WriteLine("ERROR: " + error2.ToString());
                }
                else Console.WriteLine("Command typed wrongly.");
                break;

                case "end":
                ended = true;
                break;

                default:
                Console.WriteLine($"There's no such thing as a command called {command}");
                break;
            }
        }
    }

    static void Tick(int sinceLastFull, InternalDictionary32<int, TestInput> inputs)
    {
        var internalList = inputs.GetInternalList();

        Console.WriteLine($"Current moment: {CurrMoment}, Current tick: {GGPOInst.CurrentTick}, Present tick: {GGPOInst.NowTick}, Since last full tick: {sinceLastFull}");

        Console.WriteLine("Current player inputs:");

        for(int i = 0; i< internalList.Count; ++i)
        {
            Console.WriteLine($"Player: {internalList[i].Key}, with value: {internalList[i].Value.Num}");
        }

        ++CurrMoment;
    }

    static byte[] Save(byte[] buffer)
    {
        int value = CurrMoment;

        buffer[0] = (byte) value;
        buffer[1] = (byte) (value >> 8);
        buffer[2] = (byte) (value >> 0x10);
        buffer[3] = (byte) (value >> 0x18);

        return buffer;
    }

    static void Load(byte[] saveState)
    {
        CurrMoment = BitConverter.ToInt32(saveState, 0);
    }

    static byte[] GetDefaultWorldState()
    {
        var buffer = new byte[4];

        int value = 0;

        buffer[0] = (byte) value;
        buffer[1] = (byte) (value >> 8);
        buffer[2] = (byte) (value >> 0x10);
        buffer[3] = (byte) (value >> 0x18);

        return buffer;
    }

    static void PrintInConsole(string value)
    {
        Console.WriteLine(value);
    }

    static void Test()
    {

    }
}

public struct TestInput
{
    public int Num;

    public TestInput(int value)
    {
        Num = value;
    }

    public static TestInput CreateDefault() => new TestInput(0);
}