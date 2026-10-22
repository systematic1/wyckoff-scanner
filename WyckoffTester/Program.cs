namespace WyckoffTester;

class Program
{
    static void Main(string[] args)
    {
        string symbol = string.Empty;
        
        Console.WriteLine("=========== Wyckoff Analysis Logic Tester ==========");

        while (true)
        {
            Console.WriteLine();
            Console.Write("Enter the symbol to analyze (leave blank to quit) >> ");
        
            symbol = Console.ReadLine() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(symbol))
                break;
        
            // Load the symbol data (both backfill and last update) into the database if
            // it's not already there
        
            // Run the analysis routine
        
            // Display the results the output
            // Display the same info that is put in the email message
            
            // Do not send email even if enable
            
        }
        
        Console.WriteLine();
    }
}