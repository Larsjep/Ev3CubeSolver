using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EV3CubeSolver
{
    public abstract class RobotBase
    {
        //protected IRobotLogger logger;

        private double avgLoopTime = 0.02;
        private double sleepLoopTime = 0.08;

        private bool run = true;

        protected double AvgLoopTimer
        {
            get { return avgLoopTime; }
        }

        protected RobotBase(double initialLoopTime, double loopSleepTime)
        {
            avgLoopTime = initialLoopTime;
            sleepLoopTime = loopSleepTime;
        }

        protected abstract void UpdateRobot();

        protected abstract void StartRobot();

        protected abstract void StopRobot();

        public void Run()
        {
            run = true;
            StartRobot();

            Task.Factory.StartNew(MainLoop);
        }

        public void Stop()
        {
            run = false;
            StopRobot();
        }

        private void MainLoop()
        {
            int sleepTimeMs = (int)sleepLoopTime * 100;

            while (run)
            {
                var startTime = DateTime.Now;

                UpdateRobot();

                Thread.Sleep(sleepTimeMs);

                avgLoopTime = 0.7 * avgLoopTime + 0.3 * (DateTime.Now - startTime).TotalSeconds;
            }
        }
    }
}
