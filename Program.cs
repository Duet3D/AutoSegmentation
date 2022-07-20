using DuetAPI;
using DuetAPI.Commands;
using DuetAPIClient;

namespace AutoSegmentation
{
    public static class Program
    {
        static async Task Main()
        {
            // Connect to DSF first and start intercepting G0/G1 codes on the File channel
            using InterceptConnection conn = new();
            await conn.Connect(DuetAPI.Connection.InterceptionMode.Pre, new CodeChannel[] { CodeChannel.File }, new string[] { "G0", "G1" });

            // Make sure the app is terminated when requested
            using CancellationTokenSource cts = new();
            Console.CancelKeyPress += (sender, e) => cts.Cancel();

            // Process incoming codes
            double? x = null, y = null;
            long? lastFilePosition = null;
            do
            {
                Code moveCode = await conn.ReceiveCode(cts.Token);
                if (!moveCode.Flags.HasFlag(CodeFlags.IsFromMacro) && (moveCode.Parameter('X') != null || moveCode.Parameter('Y') != null))
                {
                    if (lastFilePosition == null || moveCode.FilePosition < lastFilePosition || x == null || y == null)
                    {
                        // Just started a print, don't care about the first move(s)
                        Console.WriteLine("Potentially first move, skipping");
                        if (moveCode.Parameter('X') != null)
                        {
                            x = moveCode.Parameter('X');
                        }
                        if (moveCode.Parameter('Y') != null)
                        {
                            y = moveCode.Parameter('Y');
                        }
                        lastFilePosition = moveCode.FilePosition;
                        await conn.IgnoreCode(cts.Token);
                    }
                    else
                    {
                        // Update current file position
                        Console.WriteLine("Following move, trying to interpolate...");
                        lastFilePosition = moveCode.FilePosition;

                        // Interpolate XY
                        bool moveInterpolated = false;
                        for (int i = 0; i < moveCode.Parameters.Count; i++)
                        {
                            CodeParameter param = moveCode.Parameters[i];
                            if (param.Letter == 'X')
                            {
                                Console.WriteLine("Interpolated X");
                                moveCode.Parameters[i] = new CodeParameter('X', (x.Value + param) / 2);
                                x = param;
                                moveInterpolated = true;
                            }
                            if (param.Letter == 'Y')
                            {
                                Console.WriteLine("Interpolated Y");
                                moveCode.Parameters[i] = new CodeParameter('Y', (y.Value + param) / 2);
                                y = param;
                                moveInterpolated = true;
                            }
                        }

                        if (moveInterpolated)
                        {
                            // Halve E amounts
                            for (int i = 0; i < moveCode.Parameters.Count; i++)
                            {
                                CodeParameter param = moveCode.Parameters[i];
                                if (param.Letter == 'E')
                                {
                                    if (param.Type == typeof(float))
                                    {
                                        moveCode.Parameters[i] = new CodeParameter('E', ((float)param) / 2F);
                                    }
                                    else if (param.Type == typeof(float[]))
                                    {
                                        float[] eVals = param;
                                        moveCode.Parameters[i] = new CodeParameter('E', eVals.Select(eVal => eVal / 2F).ToArray());
                                    }
                                    break;
                                }
                            }

                            // Write interpolated code
                            Console.WriteLine("Sending interpolated code");
                            moveCode.Flags |= CodeFlags.Asynchronous;       // no longer required in v3.5 and later
                            await conn.PerformCode(moveCode, cts.Token);

                            // Write restored original code.
                            // In theory we could just ignore the intercepted code here but we want to rewrite the full code stream
                            Console.WriteLine("Sending restored code");
                            for (int i = 0; i < moveCode.Parameters.Count; i++)
                            {
                                CodeParameter param = moveCode.Parameters[i];
                                if (param.Letter == 'X')
                                {
                                    moveCode.Parameters[i] = new CodeParameter('X', x.Value);
                                }
                                else if (param.Letter == 'Y')
                                {
                                    moveCode.Parameters[i] = new CodeParameter('Y', y.Value);
                                }
                            }
                            await conn.PerformCode(moveCode);

                            // Resolve the original code being intercepted
                            Console.WriteLine("Done");
                            await conn.ResolveCode(DuetAPI.ObjectModel.MessageType.Success, string.Empty);
                        }
                        else
                        {
                            // Nothing happened. Ignore the code
                            Console.WriteLine("Nothing to interpolate");
                            await conn.IgnoreCode(cts.Token);
                        }
                    }
                }
                else
                {
                    // Ignore codes from macros (e.g. homeall.g) and codes which have no X and Y parameter
                    await conn.IgnoreCode(cts.Token);
                }
            } while (!cts.IsCancellationRequested);
        }
    }
}