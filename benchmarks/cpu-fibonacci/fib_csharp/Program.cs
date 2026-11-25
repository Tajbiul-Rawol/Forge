class P {
    static long Fib(long n) => n < 2 ? n : Fib(n-1) + Fib(n-2);
    static void Main() => System.Console.WriteLine(Fib(40));
}
