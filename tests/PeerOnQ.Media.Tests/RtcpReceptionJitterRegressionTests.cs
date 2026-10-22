using System.Reflection;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class RtcpReceptionJitterRegressionTests
{
    private static readonly MethodInfo ReceivePacket = typeof(ReceptionReport).GetMethod(
        "RtpPacketReceived", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ReceptionReport packet estimator was not found.");

    [Theory]
    [InlineData(90_000_000u, 1_000_000_000u)]
    [InlineData(4_000_000_000u, 3_000_000_000u)]
    public void First_packet_seeds_transit_without_reporting_jitter(uint rtpTimestamp, uint arrivalTimestamp)
    {
        Assert.Equal(0u, new ReportProbe().Receive(rtpTimestamp, arrivalTimestamp));
    }

    [Theory]
    [InlineData(10_000u, 10_000u)]
    [InlineData(10_000u, 1_000_000u)]
    public void Matching_packet_spacing_has_no_jitter_regardless_of_clock_phase(uint rtpTimestamp, uint arrivalTimestamp)
    {
        AssertConstantSpacingHasNoJitter(rtpTimestamp, arrivalTimestamp);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(990_000u)]
    public void Real_transit_variation_preserves_the_integer_jitter_estimator(uint arrivalOffset)
    {
        var report = new ReportProbe();
        uint[] actual =
        [
            report.Receive(10_000u, 10_000u + arrivalOffset),
            report.Receive(13_000u, 13_900u + arrivalOffset),
            report.Receive(16_000u, 16_000u + arrivalOffset),
            report.Receive(19_000u, 19_000u + arrivalOffset),
        ];

        Assert.Equal(new uint[] { 0u, 56u, 109u, 102u }, actual);
    }

    [Fact]
    public void Natural_rtp_timestamp_rollover_does_not_create_jitter()
    {
        uint rtpTimestamp = uint.MaxValue - 1500u;
        AssertConstantSpacingHasNoJitter(rtpTimestamp, unchecked(rtpTimestamp + 90_000u));
    }

    [Fact]
    public void Natural_arrival_timestamp_rollover_does_not_create_jitter()
    {
        AssertConstantSpacingHasNoJitter(1000u, uint.MaxValue - 1500u);
    }

    [Fact]
    public void Same_frame_packet_spacing_still_contributes_real_jitter()
    {
        var report = new ReportProbe();
        Assert.Equal(0u, report.Receive(1_000_000u, 1_000_000u));
        Assert.Equal(5u, report.Receive(1_000_000u, 1_000_090u));
        Assert.Equal(10u, report.Receive(1_000_000u, 1_000_180u));
    }

    private static void AssertConstantSpacingHasNoJitter(uint rtpTimestamp, uint arrivalTimestamp)
    {
        var report = new ReportProbe();
        for (uint packet = 0; packet < 64; packet++)
        {
            Assert.Equal(0u, report.Receive(
                unchecked(rtpTimestamp + packet * 3000u),
                unchecked(arrivalTimestamp + packet * 3000u)));
        }
    }

    private sealed class ReportProbe
    {
        private readonly ReceptionReport _report = new(0x12345678u);
        private ushort _sequence = 100;

        public uint Receive(uint rtpTimestamp, uint arrivalTimestamp)
        {
            ReceivePacket.Invoke(_report, [_sequence++, rtpTimestamp, arrivalTimestamp]);
            return _report.GetSample(0u).Jitter;
        }
    }
}
