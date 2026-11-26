using System.Buffers;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VisualControlsBinder.Hardware;

internal static class PhotoBackdrop
{
    private const byte Surface = 0;
    private const byte Light = 1;
    private const byte Exterior = 2;
    private const byte Seen = 3;
    private const byte Keep = 4;
    private const byte OpeningCandidate = 5;
    private const byte Aperture = 6;
    private const int FeatherWeight = 16;

    private static readonly Opening[] Cm3Linkages =
    [
        new(.5367, .2871, .515, .261, .557, .314),
        new(.645, .333, .607, .304, .677, .368)
    ];
    private static readonly Opening[] VkbEyelet = [new(.585, .864, .561, .843, .608, .887)];
    private static readonly Opening[] CougarBezel = [new(.395, .490, .122, .209, .662, .739)];
    private const string R1FalconPhoto = "virpil-r1-falcon-pedals.jpg";
    private const string R1LegendPhoto = "virpil-r1-legend-pedals.jpg";
    private static readonly Opening[] R1FalconOpenings =
    [
        new(.826, .289, .772, .245, .883, .330), new(.720, .304, .662, .260, .777, .346),
        new(.840, .353, .782, .316, .893, .398), new(.744, .369, .695, .330, .788, .408),
        new(.266, .384, .197, .331, .348, .417), new(.136, .397, .069, .350, .203, .446),
        new(.241, .425, .203, .410, .279, .474), new(.112, .481, .082, .441, .145, .521),
        new(.564, .419, .455, .388, .675, .451), new(.364, .451, .340, .434, .388, .468),
        new(.663, .482, .627, .452, .697, .512), new(.845, .562, .819, .534, .868, .590)
    ];
    private static readonly Opening[] R1LegendOpenings =
    [
        new(.774, .302, .740, .264, .805, .335), new(.784, .349, .746, .326, .820, .370),
        new(.583, .386, .483, .351, .684, .423), new(.708, .478, .618, .412, .795, .546),
        new(.321, .450, .254, .418, .387, .484), new(.895, .490, .871, .462, .921, .516),
        new(.855, .507, .827, .474, .885, .539), new(.811, .512, .797, .485, .827, .536),
        new(.378, .664, .351, .629, .401, .700), new(.229, .675, .206, .648, .252, .705),
        new(.330, .708, .302, .692, .356, .728), new(.244, .716, .224, .702, .263, .731),
        new(.283, .724, .246, .711, .317, .736)
    ];
    private static readonly Opening[] WinwingIcpBracket = [new(.776875, .348125, .771875, .340625, .784375, .355625)];
    private static readonly Opening[] HoneycombAlphaYokeGaps =
    [
        new(.18333, .68767, .12533, .61757, .28333, .76752),
        new(.82333, .69210, .71733, .61934, .88267, .77285)
    ];
    private static readonly Opening[] HoneycombCharliePedalOpenings =
    [
        new(.66312, .25062, .63562, .23250, .69563, .26750),
        new(.60437, .25812, .57625, .23875, .63250, .27563),
        new(.68063, .29187, .64812, .26813, .71250, .31500),
        new(.61313, .30125, .58000, .27687, .64687, .32500),
        new(.22375, .30938, .19187, .29000, .26250, .32875),
        new(.15250, .31938, .11812, .29750, .18563, .33875),
        new(.69500, .33875, .66250, .31562, .72937, .36188),
        new(.62813, .34875, .59375, .32562, .66312, .37250),
        new(.23187, .35437, .20312, .32875, .26250, .38188),
        new(.16000, .36688, .12062, .34000, .19937, .39375),
        new(.71437, .37438, .68125, .35938, .74313, .38938),
        new(.65812, .38312, .62625, .37000, .68625, .39750),
        new(.23438, .40750, .21688, .38562, .24938, .43312),
        new(.17375, .42000, .13500, .39313, .21438, .44625),
        new(.19062, .46125, .15125, .44312, .23062, .47875)
    ];
    private const string TprFacePhoto = "tm-tpr-oem-front.jpg";
    private const string TprOverheadPhoto = "tm-tpr-oem-overhead.jpg";
    private static readonly Opening[] TprFaceOpenings =
    [
        new(.153, .438, .1, .378, .3, .584),
        new(.199, .407, .1, .378, .3, .584),
        new(.237, .407, .1, .378, .3, .584),
        new(.273, .441, .1, .378, .3, .584),
        new(.133, .491, .1, .378, .3, .584),
        new(.183, .495, .1, .378, .3, .584),
        new(.234, .499, .1, .378, .3, .584),
        new(.28, .503, .1, .378, .3, .584),
        new(.124, .553, .1, .378, .3, .584),
        new(.171, .556, .1, .378, .3, .584),
        new(.223, .561, .1, .378, .3, .584),
        new(.268, .568, .1, .378, .3, .594),
        new(.767, .478, .7, .428, .935, .656),
        new(.806, .454, .7, .428, .935, .656),
        new(.854, .456, .7, .428, .935, .656),
        new(.889, .481, .7, .428, .935, .656),
        new(.742, .54, .7, .428, .935, .656),
        new(.797, .545, .7, .428, .935, .656),
        new(.851, .549, .7, .428, .935, .656),
        new(.909, .553, .7, .428, .935, .656),
        new(.735, .605, .7, .428, .935, .656),
        new(.792, .61, .7, .428, .935, .656),
        new(.847, .614, .7, .428, .935, .656),
        new(.902, .616, .7, .428, .935, .656),
        new(.385, .352, .326, .324, .4, .643),
        new(.38, .486, .326, .324, .4, .643),
        new(.347, .594, .326, .324, .4, .643),
        new(.712, .392, .676, .34, .735, .639),
        new(.704, .518, .676, .34, .735, .639),
        new(.689, .608, .676, .34, .735, .639)
    ];
    private static readonly Opening[] TprOverheadOpenings =
    [
        new(.079, .498, .04, .454, .267, .6),
        new(.11, .475, .04, .454, .267, .6),
        new(.162, .475, .04, .454, .267, .6),
        new(.211, .499, .04, .454, .267, .6),
        new(.073, .533, .04, .454, .267, .6),
        new(.127, .533, .04, .454, .267, .6),
        new(.187, .533, .04, .454, .267, .6),
        new(.243, .533, .04, .454, .267, .6),
        new(.084, .569, .04, .454, .267, .6),
        new(.137, .568, .04, .454, .267, .6),
        new(.195, .568, .04, .454, .267, .6),
        new(.237, .564, .04, .454, .267, .6),
        new(.785, .499, .744, .456, .956, .6),
        new(.83, .479, .744, .456, .956, .6),
        new(.876, .478, .744, .456, .956, .6),
        new(.917, .501, .744, .456, .956, .6),
        new(.756, .535, .744, .456, .956, .6),
        new(.812, .535, .744, .456, .956, .6),
        new(.869, .535, .744, .456, .956, .6),
        new(.926, .534, .744, .456, .956, .6),
        new(.755, .567, .744, .456, .956, .6),
        new(.797, .57, .744, .456, .956, .6),
        new(.853, .572, .744, .456, .956, .6),
        new(.91, .571, .744, .456, .956, .6)
    ];
    private static readonly Opening[] VkbPremiumIndexGap = [new(.530, .265, .510, .246, .548, .282)];
    private static readonly Opening[] TfrpFrontOpenings =
    [
        new(.22842, .71342, .16333, .68167, .29333, .74667),
        new(.76508, .71342, .69917, .67417, .83000, .74333),
        new(.34508, .30258, .30417, .26417, .37583, .34333),
        new(.65008, .30758, .61833, .27000, .69000, .34667),
        new(.16008, .30342, .12500, .26583, .20250, .34500),
        new(.83508, .30675, .79250, .27000, .87083, .34667),
        new(.25758, .20925, .19000, .19167, .32500, .22750),
        new(.73842, .21508, .67000, .19833, .80500, .23250),
        new(.25175, .31842, .21833, .27667, .28250, .35000),
        new(.74342, .32092, .71250, .28167, .77667, .35167),
        new(.35508, .50425, .34500, .47833, .36667, .53667),
        new(.64258, .50508, .63167, .47917, .65083, .53833),
        new(.87258, .65258, .86417, .63583, .87833, .67250),
        new(.12508, .65592, .11833, .64000, .13250, .67417),
        new(.87758, .71008, .87083, .69583, .88333, .72417),
        new(.11758, .71425, .11167, .70250, .12333, .72667)
    ];
    private static readonly Opening[] TfrpAngledOpenings =
    [
        new(.68110, .74210, .61900, .70200, .74200, .78100),
        new(.22510, .67510, .15800, .64900, .29300, .69900),
        new(.87510, .35210, .84200, .31300, .90700, .39500),
        new(.42710, .29310, .39700, .25400, .46000, .33600),
        new(.81310, .24610, .75000, .22600, .87800, .26600),
        new(.37210, .18810, .31200, .16800, .43400, .20800),
        new(.70510, .33010, .66700, .29300, .74900, .37300),
        new(.18510, .48410, .14900, .44400, .22400, .51800),
        new(.26710, .27210, .23000, .23500, .30900, .31600),
        new(.78610, .35710, .75200, .31500, .81100, .39100),
        new(.34310, .29810, .31000, .25600, .36700, .33300),
        new(.85510, .67110, .83900, .63700, .87800, .70800),
        new(.81310, .73510, .80200, .70500, .83000, .76000)
    ];
    private const string MfgCombatPhoto = "mfg-crosswind-v3-combat.jpg";
    private const string SemVerticalPhoto = "vkb-gnx-sem-v-front.png";
    private const string ThqVerticalPhoto = "vkb-gnx-thq-v-front.png";
    private static readonly Opening[] MfgClearances =
    [
        new(.666, .560, .604, .478, .823, .661),
        new(.543, .544, .507, .515, .590, .588),
        new(.575, .506, .548, .488, .591, .520),
        new(.662, .504, .649, .480, .675, .524),
        new(.604, .628, .594, .611, .615, .655),
        new(.467, .763, .446, .744, .488, .785)
    ];
    private static readonly (int X, int Y)[][] MfgCables =
    [
        [(663, 533), (700, 525), (737, 516), (780, 512), (813, 513), (845, 520),
            (870, 538), (890, 564), (900, 593), (901, 629), (896, 648)],
        [(733, 527), (754, 546), (777, 567), (799, 591), (824, 621), (844, 651), (856, 673)],
        [(733, 495), (731, 514)]
    ];

    public static BitmapSource Remove(BitmapSource source, string? imageRelativePath = null)
    {
        var fileName = Path.GetFileName(imageRelativePath)?.ToLowerInvariant();
        // A manufacturer image with a usable alpha channel already has its final silhouette.
        if (fileName?.EndsWith("-cutout.png", StringComparison.Ordinal) == true)
        {
            source.Freeze();
            return source;
        }
        var bitmap = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = checked(width * 4);
        var count = checked(width * height);
        var pixels = new byte[checked(count * 4)];
        bitmap.CopyPixels(pixels, stride, 0);
        // These photos have contact shadows along the lower edge of their dark housings.
        var upperBackdropFloor = fileName is "sony-dualshock-3-shoulders.png" or "microsoft-xbox-360-face.png" ? 173 : 226;
        var lowerBackdropFloor = fileName switch
        {
            "virpil-cm3-index-side.jpg" or "virpil-panel-1.jpg" or "virpil-panel-2.jpg"
                or "virpil-panel-2-gear.jpg" or "virpil-panel-3.jpg" => 120,
            "vkb-gnx-sem-angled.png" => 145,
            "vkb-t-rudder-mk4-pedals.png" => 195,
            "vkb-t-rudder-mk5-pedals.jpg" => 155,
            "microsoft-xbox-360-face.png" or "microsoft-xbox-series-face.jpg" => 130,
            _ => upperBackdropFloor
        };
        var lowerHalf = count / 2;
        var falconBrightFootplates = fileName == R1FalconPhoto ? (int)(height * .545) * width : 0;
        var vkbPedalChassis = fileName == "vkb-t-rudder-mk5-overhead.jpg" ? (int)(height * .408) * width : count;
        var isTpr = fileName is TprFacePhoto or TprOverheadPhoto;
        (double Left, double Top, double Right, double Bottom) panelWheel = fileName switch
        {
            "virpil-panel-2.jpg" => (.533, .049, .620, .145),
            "virpil-panel-2-gear.jpg" => (.795, .233, .900, .344),
            _ => (0.0, 0.0, 0.0, 0.0)
        };
        int wheelTop = (int)(height * panelWheel.Top) * width;
        int wheelBottom = (int)(height * panelWheel.Bottom) * width;
        int wheelLeft = (int)(width * panelWheel.Left);
        int wheelRight = (int)(width * panelWheel.Right);
        var semCapTop = fileName == SemVerticalPhoto ? (int)(height * .188) * width : count;
        var semCapBottom = (int)(height * .266) * width;
        var gearLeft = (int)(width * .234);
        var gearRight = (int)(width * .325);
        var modeLeft = (int)(width * .593);
        var modeRight = (int)(width * .697);
        var gearCrownLeft = (int)(width * .249);
        var gearCrownRight = (int)(width * .312);
        var gearCrownTop = (int)(height * .195) * width;
        var gearCrownBottom = (int)(height * .205) * width;
        var thqFloorStart = fileName == ThqVerticalPhoto ? (int)(height * .735) * width : count;
        var mfgFloorStart = fileName == MfgCombatPhoto ? (int)(height * .795) * width : count;
        var mfgFloorLeft = (int)(width * .412);
        var mfgFloorRight = (int)(width * .752);

        var regions = ArrayPool<byte>.Shared.Rent(count);
        var queue = ArrayPool<int>.Shared.Rent(count);
        byte[] output;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var offset = i * 4;
                ColorRange(pixels, offset, out var darkest, out var lightest);
                int backdropFloor = i >= lowerHalf ? lowerBackdropFloor : upperBackdropFloor;
                if (i < falconBrightFootplates)
                    backdropFloor = 244;
                if (i >= vkbPedalChassis)
                    backdropFloor = 168;
                if (isTpr)
                    backdropFloor = TprBackdropFloor(fileName == TprFacePhoto,
                        (double)(i % width) / width, (double)(i / width) / height);
                if (i >= wheelTop && i < wheelBottom && i % width >= wheelLeft && i % width <= wheelRight)
                    backdropFloor = 249;
                if (i >= mfgFloorStart)
                {
                    int x = i % width;
                    backdropFloor = x >= mfgFloorLeft && x <= mfgFloorRight ? 145 : 195;
                }
                if (i >= thqFloorStart)
                    backdropFloor = 36;
                if (i >= semCapTop && i < semCapBottom)
                {
                    // The white caps rise above the black enclosure in this straight-on photo.
                    int x = i % width;
                    if (x >= gearLeft && x <= gearRight || x >= modeLeft && x <= modeRight)
                        backdropFloor = 254;
                    // Fill the overexposed highlight inside the known cap outline.
                    if (i >= gearCrownTop && i < gearCrownBottom && x >= gearCrownLeft && x <= gearCrownRight)
                        backdropFloor = 256;
                }
                regions[i] = pixels[offset + 3] < 16 || darkest >= backdropFloor && lightest - darkest <= 28
                    ? Light
                    : Surface;
            }

            MarkExterior(regions, queue, width, height);
            if (fileName == MfgCombatPhoto)
                PreserveMfgCables(regions, width, height);
            MarkKnownOpenings(regions, queue, width, height, fileName);
            KeepSilhouette(regions, queue, width, height, count);
            if (fileName == "honeycomb-foxtrot-kit-linked.jpg")
            {
                // The manufacturer's overview shows two separate housings in the linked kit.
                int throttleSeed = (int)(height * .694) * width + (int)(width * .785);
                Fill(regions, queue, throttleSeed, width, height, Seen, Seen, Keep);
            }
            output = FeatherSilhouette(pixels, regions, width, height);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<byte>.Shared.Return(regions);
        }

        var result = BitmapSource.Create(width, height, bitmap.DpiX, bitmap.DpiY, PixelFormats.Pbgra32, null, output, stride);
        result.Freeze();
        return result;
    }

    private static int TprBackdropFloor(bool front, double x, double y)
    {
        // The base is black. Remove the contact shadow more aggressively below the bright toe rails.
        if (front ? y >= .74 : y >= .697 || y >= .688 && x is > .29 and < .72)
            return 165;
        if (front)
            return y is > .54 and < .59 && x is > .253 and < .29 ? 236 : 250;

        // The maker's overhead photo blows out the two polished arms. Keep the silver inside their traced edges.
        if (y is >= .313 and <= .555)
        {
            double along = (y - .313) / .242;
            if (x >= .279 - along * .018 && x <= .310 + along * .004 ||
                x >= .690 + along * .006 && x <= .721 + along * .022)
                return 256;
        }
        return y is >= .519 and <= .545 && (x is >= .049 and <= .257 || x is >= .747 and <= .95) ? 232 : 250;
    }

    private static void MarkKnownOpenings(byte[] regions, int[] queue, int width, int height, string? fileName)
    {
        var openings = fileName switch
        {
            "virpil-cm3-base-thumb.png" => Cm3Linkages,
            "vkb-scg-trigger.png" => VkbEyelet,
            "thrustmaster-mfd-cougar-bezel.jpg" => CougarBezel,
            R1FalconPhoto => R1FalconOpenings,
            R1LegendPhoto => R1LegendOpenings,
            "winwing-icp-panel.jpg" => WinwingIcpBracket,
            "honeycomb-alpha-face.jpg" => HoneycombAlphaYokeGaps,
            "honeycomb-charlie-face.jpg" => HoneycombCharliePedalOpenings,
            TprFacePhoto => TprFaceOpenings,
            TprOverheadPhoto => TprOverheadOpenings,
            "vkb-gunfighter-scg-premium-index-side.png" => VkbPremiumIndexGap,
            "tm-tfrp-front.jpg" => TfrpFrontOpenings,
            "tm-tfrp-angled.jpg" => TfrpAngledOpenings,
            MfgCombatPhoto => MfgClearances,
            _ => Array.Empty<Opening>()
        };

        foreach (var opening in openings)
        {
            var seed = (int)(opening.Y * height) * width + (int)(opening.X * width);
            if (regions[seed] != Light)
                continue;

            var area = Fill(regions, queue, seed, width, height, Light, Light, OpeningCandidate);
            var left = (int)(opening.Left * width);
            var top = (int)(opening.Top * height);
            var right = (int)(opening.Right * width);
            var bottom = (int)(opening.Bottom * height);
            var marker = Aperture;
            // Only remove a light island if it fits entirely inside the known aperture.
            for (var i = 0; i < area; i++)
            {
                var x = queue[i] % width;
                var y = queue[i] / width;
                if (x < left || x > right || y < top || y > bottom)
                {
                    marker = Light;
                    break;
                }
            }
            for (var i = 0; i < area; i++)
                regions[queue[i]] = marker;
        }
    }

    private static void PreserveMfgCables(byte[] regions, int width, int height)
    {
        // The white wires overlap the background through the cutouts. Keep their traced narrow paths
        // outside the aperture flood fill, using coordinates from this exact manufacturer photo.
        const int referenceWidth = 1456;
        const int referenceHeight = 999;
        foreach (var wire in MfgCables)
        {
            for (int segment = 1; segment < wire.Length; segment++)
            {
                var (startX, startY) = wire[segment - 1];
                var (endX, endY) = wire[segment];
                int steps = Math.Max(Math.Abs(endX - startX), Math.Abs(endY - startY));
                for (int step = 0; step <= steps; step++)
                {
                    int centerX = (startX + (endX - startX) * step / steps) * width / referenceWidth;
                    int centerY = (startY + (endY - startY) * step / steps) * height / referenceHeight;
                    int radius = Math.Max(2, width * 8 / referenceWidth);
                    for (int y = centerY - radius; y <= centerY + radius; y++)
                    for (int x = centerX - radius; x <= centerX + radius; x++)
                        if (x >= 0 && x < width && y >= 0 && y < height && regions[y * width + x] == Light)
                            regions[y * width + x] = Surface;
                }
            }
        }
    }

    private static void MarkExterior(byte[] regions, int[] queue, int width, int height)
    {
        void Visit(int index)
        {
            if (regions[index] == Light)
                Fill(regions, queue, index, width, height, Light, Light, Exterior);
        }

        for (var x = 0; x < width; x++)
        {
            Visit(x);
            Visit((height - 1) * width + x);
        }
        for (var y = 1; y < height - 1; y++)
        {
            Visit(y * width);
            Visit(y * width + width - 1);
        }
    }

    private static void KeepSilhouette(byte[] regions, int[] queue, int width, int height, int count)
    {
        // A bright enclosed button belongs to the silhouette. A disconnected printed logo does not.
        var largest = 0;
        var largestStart = 0;
        for (var i = 0; i < count; i++)
        {
            if (regions[i] >= Exterior)
                continue;

            var area = Fill(regions, queue, i, width, height, Surface, Light, Seen);
            if (area > largest)
            {
                largest = area;
                largestStart = i;
            }
        }

        if (largest != 0)
            Fill(regions, queue, largestStart, width, height, Seen, Seen, Keep);
    }

    private static int Fill(byte[] regions, int[] queue, int seed, int width, int height,
        byte minimum, byte maximum, byte marker)
    {
        var tail = 0;
        void Add(int i)
        {
            if (regions[i] < minimum || regions[i] > maximum)
                return;
            regions[i] = marker;
            queue[tail++] = i;
        }

        Add(seed);
        for (var head = 0; head < tail; head++)
        {
            var i = queue[head];
            var x = i % width;
            if (x > 0) Add(i - 1);
            if (x < width - 1) Add(i + 1);
            if (i >= width) Add(i - width);
            if (i < width * (height - 1)) Add(i + width);
        }

        return tail;
    }

    private static byte[] FeatherSilhouette(byte[] pixels, byte[] regions, int width, int height)
    {
        var output = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                byte region = regions[index];
                if (region is not (Keep or Exterior or Aperture))
                    continue;

                bool inside = region == Keep;
                int coverage = EdgeCoverage(regions, width, height, x, y, inside);
                if (coverage == 0)
                    continue;

                int offset = index * 4;
                // Preserve all color on the original subject. Only the new exterior feather borrows its color.
                int color = inside ? offset : FindEdgeColor(pixels, regions, width, height, x, y);
                int opacity = (pixels[color + 3] * coverage + FeatherWeight / 2) / FeatherWeight;
                output[offset + 3] = (byte)opacity;
                output[offset] = (byte)((pixels[color] * opacity + 127) / 255);
                output[offset + 1] = (byte)((pixels[color + 1] * opacity + 127) / 255);
                output[offset + 2] = (byte)((pixels[color + 2] * opacity + 127) / 255);
            }
        }

        return output;
    }

    private static int EdgeCoverage(byte[] regions, int width, int height, int centerX, int centerY, bool inside)
    {
        // The 3×3 kernel applies only one native pixel from the connected exterior, including on high-resolution photos.
        int coverage = 0;
        bool touchesExterior = false;
        for (int y = Math.Max(0, centerY - 1); y <= Math.Min(height - 1, centerY + 1); y++)
        {
            for (int x = Math.Max(0, centerX - 1); x <= Math.Min(width - 1, centerX + 1); x++)
            {
                byte region = regions[y * width + x];
                if (region == Keep)
                    coverage += (y == centerY ? 2 : 1) * (x == centerX ? 2 : 1);
                else if (region is Exterior or Aperture)
                    touchesExterior = true;
            }
        }

        return inside && !touchesExterior ? FeatherWeight : coverage;
    }

    private static int FindEdgeColor(byte[] pixels, byte[] regions, int width, int height, int centerX, int centerY)
    {
        // Use foreground color when extending the feather, so transparent edges carry no white matte.
        var best = int.MaxValue;
        var color = (centerY * width + centerX) * 4;
        for (var y = Math.Max(0, centerY - 1); y <= Math.Min(height - 1, centerY + 1); y++)
        {
            for (var x = Math.Max(0, centerX - 1); x <= Math.Min(width - 1, centerX + 1); x++)
            {
                var index = y * width + x;
                if (regions[index] != Keep)
                    continue;

                var dx = x - centerX;
                var dy = y - centerY;
                var score = dx * dx + dy * dy + (IsPaleMatte(pixels, index * 4) ? 3 : 0);
                if (score < best)
                {
                    best = score;
                    color = index * 4;
                }
            }
        }

        return color;
    }

    private static bool IsPaleMatte(byte[] pixels, int offset)
    {
        ColorRange(pixels, offset, out var darkest, out var lightest);
        return darkest >= 155 && lightest - darkest <= 28;
    }

    private static void ColorRange(byte[] pixels, int offset, out int darkest, out int lightest)
    {
        var blue = pixels[offset];
        var green = pixels[offset + 1];
        var red = pixels[offset + 2];
        darkest = Math.Min(blue, Math.Min(green, red));
        lightest = Math.Max(blue, Math.Max(green, red));
    }

    private readonly record struct Opening(double X, double Y, double Left, double Top, double Right, double Bottom);
}
