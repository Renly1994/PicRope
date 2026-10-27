using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine
{
    internal sealed class EnvelopeIcon : Image
    {
        readonly SpringMotion[] letters = {
            new SpringMotion(0.38, 0.8, 0), new SpringMotion(0.38, 0.8, 0), new SpringMotion(0.38, 0.8, 0)
        };
        static readonly BitmapSource paper = LoadPaper();
        Geometry pocket, back, lining;
        ImageBrush liningTexture;
        double paperTop;
        bool expanded;
        double sinceEnter;
        internal double Expansion { get { return letters[1].Position; } }
        internal bool Animating
        {
            get
            {
                foreach (var motion in letters)
                    if (!motion.Settled || motion.Position != (expanded ? 1 : 0)) return true;
                return false;
            }
        }
        internal Rect BodyBounds
        {
            get
            {
                double scale = Math.Min(78 / Source.Width, ActualHeight / Source.Height);
                double width = Source.Width * scale, height = Source.Height * scale;
                return new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
            }
        }

        public void Apply(LineTheme theme)
        {
            var source = TextEnvelope.Source(theme);
            Source = source;
            bool classic = theme.Id == "classic" || theme.Id == "graphite";
            bool porcelain = theme.Id == "porcelain";
            paperTop = classic ? 0.215 : (porcelain ? 0.16 : 0.18);
            // 后层剔除原图中自带的信纸，静止和展开始终使用同一组完整纸张。
            var oldLetters = new GeometryGroup { FillRule = FillRule.Nonzero };
            oldLetters.Children.Add(new RectangleGeometry(new Rect(0.125, paperTop - 0.005, 0.76, 0.58)));
            if (classic || porcelain) oldLetters.Children.Add(new RectangleGeometry(new Rect(porcelain ? 0.25 : 0.315,
                porcelain ? 0.06 : 0.145, porcelain ? 0.68 : 0.585, 0.59)));
            back = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, 1, 1)),
                oldLetters);
            back.Freeze();
            var inside = Geometry.Parse("M0.01,0.37 L0.5,0.005 L0.99,0.37 L0.99,0.98 L0.01,0.98 Z");
            lining = new CombinedGeometry(GeometryCombineMode.Intersect, oldLetters, inside); lining.Freeze();
            var texture = new CroppedBitmap(source, new Int32Rect((int)(source.PixelWidth * 0.465), (int)(source.PixelHeight * 0.025),
                (int)(source.PixelWidth * 0.07), (int)(source.PixelHeight * 0.025)));
            texture.Freeze(); liningTexture = new ImageBrush(texture); liningTexture.Freeze();
            // 用原素材的前袋遮挡信纸，保留纸纹、折痕和各套皮肤的颜色。
            var mask = new StreamGeometry();
            using (var path = mask.Open())
            {
                double mouth = theme.Id == "porcelain" ? 0.35 : 0.37;
                path.BeginFigure(new Point(0, mouth), true, true);
                double join = theme.Id == "porcelain" ? 0.636 : 0.65;
                double fold = classic ? 0.59 : (theme.Id == "porcelain" ? 0.55 : 0.57);
                path.LineTo(new Point(0.41, join), true, false);
                path.QuadraticBezierTo(new Point(0.5, fold), new Point(0.59, join), true, false);
                path.LineTo(new Point(1, mouth), true, false);
                path.LineTo(new Point(1, 1), true, false);
                path.LineTo(new Point(0, 1), true, false);
            }
            mask.Freeze(); pocket = mask;
            InvalidateVisual();
        }

        public void Expand(bool value)
        {
            expanded = value; sinceEnter = 0;
        }

        public void Reset()
        {
            expanded = false;
            foreach (var motion in letters) motion.Snap(0);
            InvalidateVisual();
        }

        public void Frame(double seconds)
        {
            sinceEnter += seconds;
            bool changed = false;
            for (int i = 0; i < letters.Length; i++)
            {
                letters[i].Target = expanded && sinceEnter >= i * 0.045 ? 1 : 0;
                double before = letters[i].Position;
                letters[i].Step(seconds);
                changed |= before != letters[i].Position;
            }
            if (changed) InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            if (Source == null) return;
            Rect body = BodyBounds;
            drawing.PushTransform(new MatrixTransform(body.Width, 0, 0, body.Height, body.X, body.Y));
            // 顶部预留抽出空间，信纸两侧和下半部分留在信封内。
            drawing.PushClip(new RectangleGeometry(new Rect(0, -0.12, 1, 1.12)));
            drawing.PushClip(back);
            drawing.DrawImage(Source, new Rect(0, 0, 1, 1));
            drawing.Pop();
            drawing.DrawGeometry(liningTexture, null, lining);
            drawing.PushClip(new RectangleGeometry(new Rect(0.06, -0.12, 0.9, 1.06)));
            // 中间一张最后绘制，三张信纸依次向上抽出。
            for (int layer = 0; layer < 3; layer++)
            {
                int i = layer == 1 ? 2 : (layer == 2 ? 1 : 0);
                double amount = Math.Max(0, Math.Min(1, letters[i].Position));
                double angle = i == 0 ? -3 : (i == 2 ? 4 : 0);
                double lift = -(i == 0 ? 0.18 : (i == 2 ? 0.13 : 0.2)) * amount;
                double top = i == 2 ? paperTop - 0.07 : paperTop + (i == 0 ? 0.025 : 0);
                var letter = new Rect(i == 2 ? 0.24 : (i == 0 ? 0.15 : 0.125), top, i == 1 ? 0.75 : 0.65, i == 1 ? 0.89 : 0.77);
                drawing.PushTransform(new TranslateTransform(0, lift));
                drawing.PushTransform(new RotateTransform(angle, 0.5, 0.78));
                drawing.DrawImage(paper, letter);
                drawing.Pop(); drawing.Pop();
            }
            drawing.Pop();
            drawing.PushClip(pocket);
            drawing.DrawImage(Source, new Rect(0, 0, 1, 1));
            drawing.Pop(); drawing.Pop(); drawing.Pop();
        }

        static BitmapSource LoadPaper()
        {
            return SkinImages.Crop("PicRope.Envelope.letter", new Int32Rect(212, 115, 830, 1055));
        }
    }
}
