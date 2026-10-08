#if UNITY_EDITOR && META_VOICE_CHAT_TESTS
using System;
using MetaVoiceChat.Input.Mic;
using NUnit.Framework;

namespace MetaVoiceChat.Tests.Editor
{
    /// <summary>Drives VcMic's read budget the way CoRecord does, against a simulated capture device.</summary>
    public sealed class VcMicReadBudgetTests
    {
        private const double PassSeconds = 1.0 / 60;
        private const double RunSeconds = 10;

        // A pass leaves at most one unread frame, because a frame is read only once the device is past its end.
        // The second frame is slack.
        private const int MaxLagFrames = 2;

        private sealed class Reader
        {
            private readonly int clipRate;
            private readonly int frameSamples;
            private double budget;
            private double now;

            public int ReadPos;
            public int FramesRead;

            public Reader(int clipRate = 48_000, int frameSamples = 960)
            {
                this.clipRate = clipRate;
                this.frameSamples = frameSamples;
                budget = clipRate;
            }

            public int DevicePos(double seconds, double speed = 1) => (int)(seconds * clipRate * speed);

            public int LagAt(double seconds) => DevicePos(seconds) - ReadPos;

            /// <summary>One CoRecord pass at <paramref name="at"/> seconds. Returns the frames read.</summary>
            public int Pass(double at, int devicePos)
            {
                budget = VcMic.EditorTestAdapter.Refill(budget, at - now, clipRate, clipRate);
                now = at;
                int frames = 0;
                while (true)
                {
                    ReadPos = VcMic.EditorTestAdapter.Resync(ReadPos, devicePos, clipRate, frameSamples);
                    int next = ReadPos + frameSamples;
                    if (next >= devicePos || budget < frameSamples)
                    {
                        break;
                    }

                    ReadPos = next;
                    budget -= frameSamples;
                    frames++;
                }

                FramesRead += frames;
                return frames;
            }
        }

        [Test]
        public void Resync_SkipsSamplesMoreThanOneRingBehind()
        {
            Assert.That(VcMic.EditorTestAdapter.Resync(0, 4 * 48_000, 48_000, 960), Is.EqualTo(3 * 48_000 + 960));
        }

        [Test]
        public void Resync_TrimsOnlyOnceTheBacklogPassesTheRingLessTheMargin()
        {
            int curr = 3 * 48_000;
            int atTheEdge = curr - (48_000 - 960);

            Assert.That(VcMic.EditorTestAdapter.Resync(atTheEdge, curr, 48_000, 960), Is.EqualTo(atTheEdge));
            Assert.That(VcMic.EditorTestAdapter.Resync(atTheEdge - 1, curr, 48_000, 960), Is.EqualTo(atTheEdge));
        }

        [Test]
        public void Refill_IsCappedAtOneRing()
        {
            Assert.That(VcMic.EditorTestAdapter.Refill(0, 10, 48_000, 48_000), Is.EqualTo(48_000));
        }

        [TestCase(48_000, 960)]
        [TestCase(96_000, 960)]
        [TestCase(44_100, 882)]
        public void PacedDevice_IsKeptUpWith(int clipRate, int frameSamples)
        {
            Reader reader = new(clipRate, frameSamples);
            for (int step = 1; step <= RunSeconds / PassSeconds; step++)
            {
                double at = step * PassSeconds;
                reader.Pass(at, reader.DevicePos(at));
                Assert.That(reader.LagAt(at), Is.LessThanOrEqualTo(MaxLagFrames * frameSamples), $"behind at {at:F2} s");
            }
        }

        [Test]
        public void BacklogOverOneRing_IsClearedWithinASecond()
        {
            // 75 frames waiting is 1.5 rings, as after a freeze of about 1.5 s.
            Reader reader = new();
            double freezeEnd = 5;
            reader.ReadPos = reader.DevicePos(freezeEnd) - 75 * 960;

            for (int step = 0; step < 1 / PassSeconds; step++)
            {
                double at = freezeEnd + step * PassSeconds;
                reader.Pass(at, reader.DevicePos(at));
            }

            Assert.That(reader.LagAt(freezeEnd + 1), Is.LessThanOrEqualTo(MaxLagFrames * 960));
        }

        [Test]
        public void RepeatedHitches_AreEachReadBackWithoutLingeringLag()
        {
            // Three one second freezes, half a second apart. The budget has to be there for each one because it
            // refills during the freeze itself.
            (double start, double length)[] hitches = { (3, 1), (4.5, 1), (6, 1) };
            Reader reader = new();
            double at = 0;
            int next = 0;
            while (at < 12)
            {
                at += PassSeconds;
                if (next < hitches.Length && at >= hitches[next].start)
                {
                    at = hitches[next].start + hitches[next].length;
                    next++;
                }

                reader.Pass(at, reader.DevicePos(at));
                Assert.That(reader.LagAt(at), Is.LessThanOrEqualTo(MaxLagFrames * 960), $"behind at {at:F2} s");
            }
        }

        [TestCase(48_000, 960)]
        [TestCase(96_000, 960)]
        public void DeviceFasterThanRealTime_IsReadAtAboutRealTime(int clipRate, int frameSamples)
        {
            Reader reader = new(clipRate, frameSamples);
            int mostInOnePass = 0;
            for (int step = 1; step <= RunSeconds / PassSeconds; step++)
            {
                double at = step * PassSeconds;
                mostInOnePass = Math.Max(mostInOnePass, reader.Pass(at, reader.DevicePos(at, speed: 20)));
            }

            double ringFrames = (double)clipRate / frameSamples;
            Assert.That(reader.FramesRead, Is.GreaterThanOrEqualTo(RunSeconds * ringFrames), "still a full real-time stream");
            Assert.That(reader.FramesRead, Is.LessThanOrEqualTo(ringFrames + 1.15 * RunSeconds * ringFrames), "no faster than real time plus drift");
            Assert.That(mostInOnePass, Is.LessThanOrEqualTo(ringFrames), "no pass reads more than one ring");
        }
    }
}
#endif
