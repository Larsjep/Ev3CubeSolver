using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KociembaTwoPhase;
using MonoBrickFirmware.Display;
using MonoBrickFirmware.UserInput;

namespace EV3CubeSolver
{
    static class Program
    {
        static void Main(string[] args)
        {
            var terminateProgram = new ManualResetEvent(false);
            var cubeSolver = new CubeSolver();
            var buts = new ButtonEvents();

            buts.EscapePressed += () => terminateProgram.Set();

            Task.Factory.StartNew(cubeSolver.Run);

            terminateProgram.WaitOne();
            cubeSolver.Stop();

            //var solution = Search.solution(Tools.randomCube(), 26, false);
        }
    }
}
