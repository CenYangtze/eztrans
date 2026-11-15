using System.Text.RegularExpressions;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Ocr;

namespace SnapTrans.Core.Layout;

public sealed class LineSplitter(LayoutOptions? options=null)
{
    private readonly LayoutOptions _options=options??new();
    public IReadOnlyList<OcrLine> Split(OcrLine line)
    {
        if(line.Words.Count==0) return [line];
        var output=new List<OcrLine>(); var words=new List<OcrWord>();
        foreach(var word in line.Words.OrderBy(w=>w.Bounds.Left))
        {
            if(words.Count>0 && word.Bounds.Left-words[^1].Bounds.Right > _options.WordGapHeightRatio*Math.Max(1,line.Bounds.Height)) { output.Add(Make(words)); words=[]; }
            words.Add(word);
        }
        if(words.Count>0) output.Add(Make(words));
        return output;
    }
    private static OcrLine Make(IReadOnlyList<OcrWord> words) => new(OcrText.JoinWords(words.Select(w=>w.Text)),words.Select(w=>w.Bounds).Aggregate(PixelRectF.Union),words.ToArray());
}

public sealed partial class BlockBuilder(LayoutOptions? options=null)
{
    private readonly LayoutOptions _options=options??new();
    public IReadOnlyList<TextBlock> Build(OcrPage page)
    {
        var splitter=new LineSplitter(_options);
        var lines=page.Lines.SelectMany(splitter.Split).Where(l=>!l.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l=>l.Bounds.Top).ThenBy(l=>l.Bounds.Left).ToList();
        // Build row bands before x ordering; avoid a nontransitive fuzzy comparison.
        var sorted=new List<OcrLine>();
        while(lines.Count>0)
        {
            var first=lines[0]; var row=lines.Where(l=>Math.Abs(l.Bounds.Top-first.Bounds.Top)<Math.Max(1,Math.Min(l.Bounds.Height,first.Bounds.Height)*_options.RowToleranceRatio)).OrderBy(l=>l.Bounds.Left).ToArray();
            sorted.AddRange(row); foreach(var l in row) lines.Remove(l);
        }
        var groups=new List<List<OcrLine>>();
        foreach(var line in sorted)
        {
            List<OcrLine>? best=null; float bestGap=float.MaxValue;
            if(!ListMarker().IsMatch(line.Text))
            foreach(var group in groups)
            {
                var previous=group[^1];var bounds=group.Select(l=>l.Bounds).Aggregate(PixelRectF.Union);
                float h=Math.Max(previous.Bounds.Height,line.Bounds.Height),gap=line.Bounds.Top-previous.Bounds.Bottom;
                if(gap<0 || gap>_options.MaxVerticalGapRatio*h || Math.Abs(line.Bounds.Height-previous.Bounds.Height)>_options.MaxHeightDifferenceRatio*h) continue;
                float overlap=Math.Max(0,Math.Min(line.Bounds.Right,previous.Bounds.Right)-Math.Max(line.Bounds.Left,previous.Bounds.Left));
                bool aligned=Math.Abs(line.Bounds.Left-bounds.Left)<=_options.LeftAlignmentToleranceRatio*h;
                if(!aligned && overlap<Math.Min(line.Bounds.Width,previous.Bounds.Width)*_options.MinimumHorizontalOverlapRatio) continue;
                // Reject a merge crossing another column's segment at the same vertical level.
                if(gap<bestGap) { best=group;bestGap=gap; }
            }
            if(best is null) groups.Add([line]);else best.Add(line);
        }
        var blocks=groups.Select(group=>
        {
            var bounds=group.Select(l=>l.Bounds).Aggregate(PixelRectF.Union);
            float height=Median(group.Select(l=>l.Bounds.Height));
            float? pitch=group.Count>1?Median(group.Zip(group.Skip(1),(a,b)=>b.Bounds.Top-a.Bounds.Top)):null;
            return new TextBlock(0,OcrText.JoinLines(group.Select(l=>l.Text)),bounds,group.ToArray(),height,pitch,Alignment(group));
        }).ToArray();
        return new ReadingOrder(_options).Sort(blocks).Select((block,index)=>block with { Id=index+1 }).ToArray();
    }
    private static float Median(IEnumerable<float> values) { var a=values.Order().ToArray();return a.Length%2==0?(a[a.Length/2-1]+a[a.Length/2])/2:a[a.Length/2]; }
    private static TextAlign Alignment(List<OcrLine> lines)
    {
        if(lines.Count==1) return TextAlign.Left;
        static double Variance(IEnumerable<float> values) { var a=values.Select(v=>(double)v).ToArray();var average=a.Average();return a.Sum(v=>(v-average)*(v-average))/a.Length; }
        var choices=new[] { (TextAlign.Left,Variance(lines.Select(l=>l.Bounds.Left))), (TextAlign.Center,Variance(lines.Select(l=>l.Bounds.Left+l.Bounds.Width/2))), (TextAlign.Right,Variance(lines.Select(l=>l.Bounds.Right))) };
        return choices.MinBy(c=>c.Item2).Item1;
    }
    [GeneratedRegex(@"^\s*(?:[•\-*]|\d+[.)]|\(\d+\))\s*")]
    private static partial Regex ListMarker();
}

public sealed class ReadingOrder(LayoutOptions? options=null)
{
    private readonly LayoutOptions _options=options??new();
    public IReadOnlyList<TextBlock> Sort(IEnumerable<TextBlock> source)
    {
        var remaining=source.OrderBy(b=>b.Bounds.Top).ThenBy(b=>b.Bounds.Left).ToList();var output=new List<TextBlock>();
        while(remaining.Count>0)
        {
            var first=remaining[0];
            var band=remaining.Where(b=>Math.Max(0,Math.Min(first.Bounds.Bottom,b.Bounds.Bottom)-Math.Max(first.Bounds.Top,b.Bounds.Top))>=Math.Min(first.Bounds.Height,b.Bounds.Height)*_options.BandOverlapRatio).OrderBy(b=>b.Bounds.Left).ToArray();
            if(band.Length==0) band=[first];
            output.AddRange(band);foreach(var item in band)remaining.Remove(item);
        }
        return output;
    }
}
