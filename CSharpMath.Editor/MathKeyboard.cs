namespace CSharpMath.Editor {
  using System;
  using System.Collections.Generic;
  using System.Drawing;
  using Atom;
  using Display;
  using Display.FrontEnd;
  using Structures;
  using Atoms = Atom.Atoms;

  public class MathKeyboard<TFont, TGlyph> where TFont : IFont<TGlyph> {
    public MathKeyboard(TypesettingContext<TFont, TGlyph> context, TFont font) =>
      (Context, Font) = (context, font);
    //private readonly List<MathListIndex> highlighted;
    protected TypesettingContext<TFont, TGlyph> Context { get; }
    public bool ShowCaret { get; protected set; }
    public Display.Displays.ListDisplay<TFont, TGlyph>? Display { get; protected set; }
    public MathList MathList { get; } = new MathList();
    public string LaTeX => LaTeXBuilder.MathListToLaTeX(MathList).ToString();
    private MathListIndex _insertionIndex = MathListIndex.Level0Index(0);
    public MathListIndex InsertionIndex { get => _insertionIndex; set { _insertionIndex = value; InsertionPointChanged(); } }
    public TFont Font { get; set; }
    public LineStyle LineStyle { get; set; }
    public Structures.Color SelectColor { get; set; }
    public RectangleF? Measure => Display?.DisplayBounds();
    public bool HasText => MathList?.Atoms?.Count > 0;
    public void RecreateDisplayFromMathList() {
      var position = Display?.Position ?? default;
      Display = Typesetter.CreateLine(MathList, Font, Context, LineStyle);
      Display.Position = position;
    }
    /// <summary>Keyboard should now be hidden and input be discarded.</summary>
    public event EventHandler? DismissPressed;
    /// <summary>Keyboard should now be hidden and input be saved.</summary>
    public event EventHandler? ReturnPressed;
    /// <summary><see cref="Display"/> should be redrawn.</summary>
    public event EventHandler? RedrawRequested;
    public PointF? ClosestPointToIndex(MathListIndex index) =>
      Display?.PointForIndex(Context, index);
    public MathListIndex? ClosestIndexToPoint(PointF point) =>
      Display?.IndexForPoint(Context, point);
    public void KeyPress(params MathKeyboardInput[] inputs) {
      foreach (var input in inputs) KeyPress(input);
    }
    public void KeyPress(MathKeyboardInput input) {
      void HandleScriptButton(bool isSuperScript) {
        var subIndexType =
          isSuperScript ? MathListSubIndexType.Superscript : MathListSubIndexType.Subscript;
        MathList? GetScript(MathAtom atom) =>
          isSuperScript ? atom.Superscript : atom.Subscript;
        void SetScript(MathAtom atom, MathList? value) { if (isSuperScript) atom.Superscript = value; else atom.Subscript = value; }
        void CreateEmptyAtom() {
          // Create an empty atom and move the insertion index up.
          var emptyAtom = LaTeXDefaults.Placeholder;
          SetScript(emptyAtom, LaTeXDefaults.PlaceholderList);
          MathList.InsertAndAdvance(ref _insertionIndex, emptyAtom, subIndexType);
        }
        static bool IsFullPlaceholderRequired(MathAtom mathAtom) =>
          mathAtom switch
          {
            Atoms.BinaryOperator _ => true,
            Atoms.UnaryOperator _ => true,
            Atoms.Relation _ => true,
            Atoms.Open _ => true,
            Atoms.Punctuation _ => true,
            _ => false
          };
        if (!(_insertionIndex.Previous is MathListIndex previous)) {
          CreateEmptyAtom();
        } else {
          var isBetweenBaseAndScripts =
            _insertionIndex.FinalSubIndexType is MathListSubIndexType.BetweenBaseAndScripts;
          var prevIndexCorrected =
            isBetweenBaseAndScripts ? _insertionIndex.LevelDown() : previous;
          if (prevIndexCorrected is null)
            throw new InvalidCodePathException("prevIndexCorrected is null");
          var prevAtom = MathList.AtomAt(prevIndexCorrected);
          if (prevAtom is null)
            throw new InvalidCodePathException("prevAtom is null");
          if (!isBetweenBaseAndScripts && IsFullPlaceholderRequired(prevAtom)) {
            CreateEmptyAtom();
          } else {
            var script = GetScript(prevAtom);
            if (script is null) {
              SetScript(prevAtom, LaTeXDefaults.PlaceholderList);
            }
            _insertionIndex = prevIndexCorrected.LevelUpWithSubIndex
              (subIndexType, MathListIndex.Level0Index(script?.Atoms?.Count ?? 0));
          }
        }
      }

      void HandleSlashButton() {
        // special / handling - makes the thing a fraction
        var numerator = new Stack<MathAtom>();
        var parenDepth = 0;
        if (_insertionIndex.FinalSubIndexType == MathListSubIndexType.BetweenBaseAndScripts)
          _insertionIndex = _insertionIndex.LevelDown()?.Next
              ?? throw new InvalidCodePathException("_insertionIndex.LevelDown() returned null");
        for (; _insertionIndex.Previous != null; _insertionIndex = _insertionIndex.Previous) {
          switch (MathList.AtomAt(_insertionIndex.Previous), parenDepth) {
            case (null, _): throw new InvalidCodePathException("Invalid _insertionIndex");
            // Stop looking behind upon encountering these atoms unparenthesized
            case (Atoms.Open _, _) when --parenDepth < 0: goto stop;
            case (Atoms.Close { HasCorrespondingOpen: true } a, _): parenDepth++; numerator.Push(a); break;
            case (Atoms.UnaryOperator _, 0): goto stop;
            case (Atoms.BinaryOperator _, 0): goto stop;
            case (Atoms.Relation _, 0): goto stop;
            case (Atoms.Fraction _, 0): goto stop;
            case (Atoms.Open _, _) when parenDepth < 0: goto stop;
            // We don't put this atom on the fraction
            case (var a, _): numerator.Push(a); break;
          }
        }
stop:   MathList.RemoveAtoms(new MathListRange(_insertionIndex, numerator.Count));
        if (numerator.Count == 0)
          // so we didn't really find any numbers before this, so make the numerator 1
          numerator.Push(new Atoms.Number("1"));
        if (MathList.AtomAt(_insertionIndex.Previous) is Atoms.Fraction)
          // Add a times symbol
          MathList.InsertAndAdvance(ref _insertionIndex, LaTeXDefaults.Times, MathListSubIndexType.None);
        MathList.InsertAndAdvance(ref _insertionIndex, new Atoms.Fraction(
          new MathList(numerator),
          LaTeXDefaults.PlaceholderList
        ), MathListSubIndexType.Denominator);
      }
      void InsertAtomPair(MathAtom left, MathAtom right) {
        MathList.InsertAndAdvance(ref _insertionIndex, left, MathListSubIndexType.None);
        MathList.InsertAndAdvance(ref _insertionIndex, right, MathListSubIndexType.None);
        // Don't go to the next insertion index, to start inserting before the close parens.
        if (_insertionIndex.FinalSubIndexType is MathListSubIndexType.BetweenBaseAndScripts)
          _insertionIndex = _insertionIndex.LevelDown()
            ?? throw new InvalidCodePathException(
              "_insertionIndex.LevelDown() returned null despite its FinalSubIndexType being BetweenBaseAndScripts");
        else
          _insertionIndex = _insertionIndex.Previous
            ?? throw new InvalidCodePathException(
              "_insertionIndex.Previous returned null despite insertions above");
      }

      void MoveCursorLeft() {
        var prev = _insertionIndex.Previous;
        switch (MathList.AtomAt(prev)) {
          case var _ when prev is null:
          case null: // At beginning of line
            var levelDown = _insertionIndex.LevelDown();
            switch (_insertionIndex.FinalSubIndexType) {
              case MathListSubIndexType.None:
                goto default;
              case var _ when levelDown is null:
                throw new InvalidCodePathException("Null levelDown despite non-None FinalSubIndexType");
              case MathListSubIndexType.Superscript:
                var scriptAtom = MathList.AtomAt(levelDown);
                if (scriptAtom is null)
                  throw new InvalidCodePathException("Invalid levelDown");
                if (scriptAtom.Subscript != null)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.Subscript,
                     MathListIndex.Level0Index(scriptAtom.Subscript.Count));
                else
                  goto case MathListSubIndexType.Subscript;
                break;
              case MathListSubIndexType.Subscript:
                _insertionIndex = levelDown.LevelUpWithSubIndex
                  (MathListSubIndexType.BetweenBaseAndScripts, MathListIndex.Level0Index(1));
                break;
              case MathListSubIndexType.BetweenBaseAndScripts:
                if (MathList.AtomAt(levelDown) is Atoms.Radical rad && rad.Radicand.Count > 0)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.Radicand,
                     MathListIndex.Level0Index(rad.Radicand.Count));
                else if (MathList.AtomAt(levelDown) is Atoms.Fraction frac && frac.Denominator != null)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.Denominator,
                     MathListIndex.Level0Index(frac.Denominator.Count));
                else goto case MathListSubIndexType.Radicand;
                break;
              case MathListSubIndexType.Radicand:
                if (MathList.AtomAt(levelDown) is Atoms.Radical radDeg && radDeg.Degree is MathList deg)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.Degree, MathListIndex.Level0Index(deg.Count));
                else
                  goto case MathListSubIndexType.Denominator;
                break;
              case MathListSubIndexType.Denominator:
                if (MathList.AtomAt(levelDown) is Atoms.Fraction fracNum && fracNum.Numerator != null)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.Numerator, MathListIndex.Level0Index(fracNum.Numerator.Count));
                else
                  goto default;
                break;
              case MathListSubIndexType.Degree:
              case MathListSubIndexType.Numerator:
              default:
                _insertionIndex = levelDown ?? _insertionIndex;
                break;
            }
            break;
          case { Superscript: { } s }:
            _insertionIndex = prev.LevelUpWithSubIndex
              (MathListSubIndexType.Superscript, MathListIndex.Level0Index(s.Count));
            break;
          case { Subscript: { } s }:
            _insertionIndex = prev.LevelUpWithSubIndex
              (MathListSubIndexType.Subscript, MathListIndex.Level0Index(s.Count));
            break;
          case Atoms.Radical { Radicand: { } r }:
            _insertionIndex = prev.LevelUpWithSubIndex
              (MathListSubIndexType.Radicand, MathListIndex.Level0Index(r.Count));
            break;
          case Atoms.Fraction { Denominator: { } d }:
            _insertionIndex = prev.LevelUpWithSubIndex
              (MathListSubIndexType.Denominator, MathListIndex.Level0Index(d.Count));
            break;
          default:
            _insertionIndex = prev;
            break;
        }
        if (_insertionIndex is null)
          throw new InvalidOperationException($"{nameof(_insertionIndex)} is null.");
        if (_insertionIndex.FinalSubIndexType is MathListSubIndexType.BetweenBaseAndScripts) {
          var prevInd = _insertionIndex.LevelDown();
          if (prevInd != null && MathList.AtomAt(prevInd) is Atoms.Placeholder)
            _insertionIndex = prevInd;
        } else if (MathList.AtomAt(_insertionIndex) is null
                   && _insertionIndex?.Previous is MathListIndex previous) {
          if (MathList.AtomAt(previous) is Atoms.Placeholder { Superscript: null, Subscript: null })
            _insertionIndex = previous; // Skip right side of placeholders when end of line
        }
      }
      void MoveCursorRight() {
        if (_insertionIndex is null)
          throw new InvalidOperationException($"{nameof(_insertionIndex)} is null.");
        switch (MathList.AtomAt(_insertionIndex)) {
          case null: //After Count
            var levelDown = _insertionIndex.LevelDown();
            var levelDownAtom = MathList.AtomAt(levelDown);
            switch (_insertionIndex.FinalSubIndexType) {
              case MathListSubIndexType.None:
                goto default;
              case var _ when levelDown is null:
                throw new InvalidCodePathException("Null levelDown despite non-None FinalSubIndexType");
              case var _ when levelDownAtom is null:
                throw new InvalidCodePathException("Invalid levelDown");
              case MathListSubIndexType.Degree:
                if (levelDownAtom is Atoms.Radical)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.Radicand, MathListIndex.Level0Index(0));
                else
                  throw new SubIndexTypeMismatchException(typeof(Atoms.Radical), levelDown);
                break;
              case MathListSubIndexType.Numerator:
                if (levelDownAtom is Atoms.Fraction)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.Denominator, MathListIndex.Level0Index(0));
                else
                  throw new SubIndexTypeMismatchException(typeof(Atoms.Fraction), levelDown);
                break;
              case MathListSubIndexType.Radicand:
              case MathListSubIndexType.Denominator:
                if (levelDownAtom.Superscript != null || levelDownAtom.Subscript != null)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.BetweenBaseAndScripts, MathListIndex.Level0Index(1));
                else
                  goto default;
                break;
              case MathListSubIndexType.BetweenBaseAndScripts:
                if (levelDownAtom.Subscript != null)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.Subscript, MathListIndex.Level0Index(0));
                else
                  goto case MathListSubIndexType.Subscript;
                break;
              case MathListSubIndexType.Subscript:
                if (levelDownAtom.Superscript != null)
                  _insertionIndex = levelDown.LevelUpWithSubIndex
                    (MathListSubIndexType.Superscript, MathListIndex.Level0Index(0));
                else
                  goto default;
                break;
              case MathListSubIndexType.Superscript:
              default:
                _insertionIndex = levelDown?.Next ?? _insertionIndex;
                break;
            }
            break;
          case var a when _insertionIndex.FinalSubIndexType is MathListSubIndexType.BetweenBaseAndScripts:
            levelDown = _insertionIndex.LevelDown();
            if (levelDown is null)
              throw new InvalidCodePathException
                ("_insertionIndex.FinalSubIndexType is BetweenBaseAndScripts but levelDown is null");
            _insertionIndex = levelDown.LevelUpWithSubIndex(
              a.Subscript != null ? MathListSubIndexType.Subscript : MathListSubIndexType.Superscript,
              MathListIndex.Level0Index(0));
            break;
          case Atoms.Fraction _:
            _insertionIndex = _insertionIndex.LevelUpWithSubIndex
              (MathListSubIndexType.Numerator, MathListIndex.Level0Index(0));
            break;
          case Atoms.Radical rad:
            _insertionIndex = _insertionIndex.LevelUpWithSubIndex(
              rad.Degree.Count > 0 ? MathListSubIndexType.Degree : MathListSubIndexType.Radicand,
              MathListIndex.Level0Index(0));
            break;
          case var a when a.Superscript != null || a.Subscript != null:
            _insertionIndex = _insertionIndex.LevelUpWithSubIndex
              (MathListSubIndexType.BetweenBaseAndScripts, MathListIndex.Level0Index(1));
            break;
          case Atoms.Placeholder _ when MathList.AtomAt(_insertionIndex.Next) is null:
            // Skip right side of placeholders when end of line
            goto case null;
          default:
            _insertionIndex = _insertionIndex.Next;
            break;
        }
        if (_insertionIndex is null)
          throw new InvalidOperationException($"{nameof(_insertionIndex)} is null.");
        if (_insertionIndex.FinalSubIndexType is MathListSubIndexType.BetweenBaseAndScripts
            && MathList.AtomAt(_insertionIndex.LevelDown()) is Atoms.Placeholder)
          MoveCursorRight();
      }

      void DeleteBackwards() {
        // delete the last atom from the list
        if (HasText && _insertionIndex.Previous is MathListIndex previous) {
          _insertionIndex = previous;
          MathList.RemoveAt(ref _insertionIndex);
        }
      }

      static bool IsPlaceholderList(MathList ml) => ml.Count == 1 && ml[0] is Atoms.Placeholder;
      void InsertAtom(MathAtom a) =>
        MathList.InsertAndAdvance(ref _insertionIndex, a,
          a switch
          {
            Atoms.Fraction _ => MathListSubIndexType.Numerator,
            Atoms.Radical { Degree: { } d } when IsPlaceholderList(d) => MathListSubIndexType.Degree,
            Atoms.Radical _ => MathListSubIndexType.Radicand,
            _ => MathListSubIndexType.None
          });
      void InsertSymbolName(string name, bool subscript = false, bool superscript = false) {
        var atom =
          LaTeXDefaults.AtomForCommand(name) ??
            throw new InvalidCodePathException("Looks like someone mistyped a symbol name...");
        InsertAtom(atom);
        switch (subscript, superscript) {
          case (true, true):
            HandleScriptButton(true);
            _insertionIndex = _insertionIndex.LevelDown()?.Next
              ?? throw new InvalidCodePathException(
                "_insertionIndex.Previous returned null despite script button handling");
            HandleScriptButton(false);
            break;
          case (false, true):
            HandleScriptButton(true);
            break;
          case (true, false):
            HandleScriptButton(false);
            break;
          case (false, false):
            break;
        }
      }

      switch (input) {
#warning Unimplemented up/down buttons
        case MathKeyboardInput.Up:
          break;
        case MathKeyboardInput.Down:
          break;
        case MathKeyboardInput.Left:
          MoveCursorLeft();
          break;
        case MathKeyboardInput.Right:
          MoveCursorRight();
          break;
        case MathKeyboardInput.Backspace:
          DeleteBackwards();
          break;
        case MathKeyboardInput.Clear:
          MathList.Clear();
          InsertionIndex = MathListIndex.Level0Index(0);
          break;
        case MathKeyboardInput.Return:
          ReturnPressed?.Invoke(this, EventArgs.Empty);
          ShowCaret = false;
          return;
        case MathKeyboardInput.Dismiss:
          DismissPressed?.Invoke(this, EventArgs.Empty);
          ShowCaret = false;
          return;
        case MathKeyboardInput.BothRoundBrackets:
          InsertAtomPair(new Atoms.Open("("), new Atoms.Close(")"));
          break;
        case MathKeyboardInput.Slash:
          HandleSlashButton();
          break;
        case MathKeyboardInput.Power:
          HandleScriptButton(true);
          break;
        case MathKeyboardInput.Subscript:
          HandleScriptButton(false);
          break;
        case MathKeyboardInput.Fraction:
          InsertAtom(LaTeXDefaults.PlaceholderFraction);
          break;
        case MathKeyboardInput.SquareRoot:
          InsertAtom(LaTeXDefaults.PlaceholderSquareRoot);
          break;
        case MathKeyboardInput.CubeRoot:
          InsertAtom(LaTeXDefaults.PlaceholderCubeRoot);
          break;
        case MathKeyboardInput.NthRoot:
          InsertAtom(LaTeXDefaults.PlaceholderRadical);
          break;
        case MathKeyboardInput.Absolute:
          InsertAtomPair(new Atoms.Ordinary("|"), new Atoms.Ordinary("|"));
          break;
        case MathKeyboardInput.BaseEPower:
          InsertAtom(LaTeXDefaults.ForAscii((sbyte)'e')
            ?? throw new InvalidCodePathException("LaTeXDefaults.ForAscii((byte)'e') is null"));
          HandleScriptButton(true);
          break;
        case MathKeyboardInput.Logarithm:
          InsertSymbolName("log");
          break;
        case MathKeyboardInput.NaturalLogarithm:
          InsertSymbolName("ln");
          break;
        case MathKeyboardInput.LogarithmWithBase:
          InsertSymbolName("log", subscript: true);
          break;
        case MathKeyboardInput.Sine:
          InsertSymbolName("sin");
          break;
        case MathKeyboardInput.Cosine:
          InsertSymbolName("cos");
          break;
        case MathKeyboardInput.Tangent:
          InsertSymbolName("tan");
          break;
        case MathKeyboardInput.Cotangent:
          InsertSymbolName("cot");
          break;
        case MathKeyboardInput.Secant:
          InsertSymbolName("sec");
          break;
        case MathKeyboardInput.Cosecant:
          InsertSymbolName("csc");
          break;
        case MathKeyboardInput.ArcSine:
          InsertSymbolName("arcsin");
          break;
        case MathKeyboardInput.ArcCosine:
          InsertSymbolName("arccos");
          break;
        case MathKeyboardInput.ArcTangent:
          InsertSymbolName("arctan");
          break;
        case MathKeyboardInput.ArcCotangent:
          InsertSymbolName("arccot");
          break;
        case MathKeyboardInput.ArcSecant:
          InsertSymbolName("arcsec");
          break;
        case MathKeyboardInput.ArcCosecant:
          InsertSymbolName("arccsc");
          break;
        case MathKeyboardInput.HyperbolicSine:
          InsertSymbolName("sinh");
          break;
        case MathKeyboardInput.HyperbolicCosine:
          InsertSymbolName("cosh");
          break;
        case MathKeyboardInput.HyperbolicTangent:
          InsertSymbolName("tanh");
          break;
        case MathKeyboardInput.HyperbolicCotangent:
          InsertSymbolName("coth");
          break;
        case MathKeyboardInput.HyperbolicSecant:
          InsertSymbolName("sech");
          break;
        case MathKeyboardInput.HyperbolicCosecant:
          InsertSymbolName("csch");
          break;
        case MathKeyboardInput.AreaHyperbolicSine:
          InsertSymbolName("arsinh");
          break;
        case MathKeyboardInput.AreaHyperbolicCosine:
          InsertSymbolName("arcosh");
          break;
        case MathKeyboardInput.AreaHyperbolicTangent:
          InsertSymbolName("artanh");
          break;
        case MathKeyboardInput.AreaHyperbolicCotangent:
          InsertSymbolName("arcoth");
          break;
        case MathKeyboardInput.AreaHyperbolicSecant:
          InsertSymbolName("arsech");
          break;
        case MathKeyboardInput.AreaHyperbolicCosecant:
          InsertSymbolName("arcsch");
          break;
        case MathKeyboardInput.LimitWithBase:
          InsertSymbolName("lim", subscript: true);
          break;
        case MathKeyboardInput.Integral:
          InsertSymbolName("int");
          break;
        case MathKeyboardInput.IntegralLowerLimit:
          InsertSymbolName("int", subscript: true);
          break;
        case MathKeyboardInput.IntegralUpperLimit:
          InsertSymbolName("int", superscript: true);
          break;
        case MathKeyboardInput.IntegralBothLimits:
          InsertSymbolName("int", subscript: true, superscript: true);
          break;
        case MathKeyboardInput.Summation:
          InsertSymbolName("sum");
          break;
        case MathKeyboardInput.SummationLowerLimit:
          InsertSymbolName("sum", subscript: true);
          break;
        case MathKeyboardInput.SummationUpperLimit:
          InsertSymbolName("sum", superscript: true);
          break;
        case MathKeyboardInput.SummationBothLimits:
          InsertSymbolName("sum", subscript: true, superscript: true);
          break;
        case MathKeyboardInput.Product:
          InsertSymbolName("prod");
          break;
        case MathKeyboardInput.ProductLowerLimit:
          InsertSymbolName("prod", subscript: true);
          break;
        case MathKeyboardInput.ProductUpperLimit:
          InsertSymbolName("prod", superscript: true);
          break;
        case MathKeyboardInput.ProductBothLimits:
          InsertSymbolName("prod", subscript: true, superscript: true);
          break;
        case MathKeyboardInput.DoubleIntegral:
          InsertSymbolName("iint");
          break;
        case MathKeyboardInput.TripleIntegral:
          InsertSymbolName("iiint");
          break;
        case MathKeyboardInput.QuadrupleIntegral:
          InsertSymbolName("iiiint");
          break;
        case MathKeyboardInput.ContourIntegral:
          InsertSymbolName("oint");
          break;
        case MathKeyboardInput.DoubleContourIntegral:
          InsertSymbolName("oiint");
          break;
        case MathKeyboardInput.TripleContourIntegral:
          InsertSymbolName("oiiint");
          break;
        case MathKeyboardInput.ClockwiseIntegral:
          InsertSymbolName("intclockwise");
          break;
        case MathKeyboardInput.ClockwiseContourIntegral:
          InsertSymbolName("varointclockwise");
          break;
        case MathKeyboardInput.CounterClockwiseContourIntegral:
          InsertSymbolName("ointctrclockwise");
          break;
        case MathKeyboardInput.LeftArrow:
          InsertSymbolName("leftarrow");
          break;
        case MathKeyboardInput.UpArrow:
          InsertSymbolName("uparrow");
          break;
        case MathKeyboardInput.RightArrow:
          InsertSymbolName("rightarrow");
          break;
        case MathKeyboardInput.DownArrow:
          InsertSymbolName("downarrow");
          break;
        case MathKeyboardInput.PartialDifferential:
          InsertSymbolName("partial");
          break;
        case MathKeyboardInput.NotEquals:
          InsertSymbolName("neq");
          break;
        case MathKeyboardInput.LessOrEquals:
          InsertSymbolName("leq");
          break;
        case MathKeyboardInput.GreaterOrEquals:
          InsertSymbolName("geq");
          break;
        case MathKeyboardInput.Multiply:
          InsertSymbolName("times");
          break;
        case MathKeyboardInput.Divide:
          InsertSymbolName("div");
          break;
        case MathKeyboardInput.Infinity:
          InsertSymbolName("infty");
          break;
        case MathKeyboardInput.Degree:
          InsertSymbolName("degree");
          break;
        case MathKeyboardInput.Angle:
          InsertSymbolName("angle");
          break;
        case MathKeyboardInput.LeftCurlyBracket:
          InsertSymbolName("{");
          break;
        case MathKeyboardInput.RightCurlyBracket:
          InsertSymbolName("}");
          break;
        case MathKeyboardInput.Percentage:
          InsertSymbolName("%");
          break;
        case MathKeyboardInput.Space:
          InsertSymbolName(" ");
          break;
        case MathKeyboardInput.Prime:
          InsertAtom(new Atoms.Prime(1));
          break;
        case MathKeyboardInput.LeftRoundBracket:
        case MathKeyboardInput.RightRoundBracket:
        case MathKeyboardInput.LeftSquareBracket:
        case MathKeyboardInput.RightSquareBracket:
        case MathKeyboardInput.D0:
        case MathKeyboardInput.D1:
        case MathKeyboardInput.D2:
        case MathKeyboardInput.D3:
        case MathKeyboardInput.D4:
        case MathKeyboardInput.D5:
        case MathKeyboardInput.D6:
        case MathKeyboardInput.D7:
        case MathKeyboardInput.D8:
        case MathKeyboardInput.D9:
        case MathKeyboardInput.Decimal:
        case MathKeyboardInput.Plus:
        case MathKeyboardInput.Minus:
        case MathKeyboardInput.Ratio:
        case MathKeyboardInput.Comma:
        case MathKeyboardInput.Semicolon:
        case MathKeyboardInput.Factorial:
        case MathKeyboardInput.VerticalBar:
        case MathKeyboardInput.Equals:
        case MathKeyboardInput.LessThan:
        case MathKeyboardInput.GreaterThan:
        case MathKeyboardInput.A:
        case MathKeyboardInput.B:
        case MathKeyboardInput.C:
        case MathKeyboardInput.D:
        case MathKeyboardInput.E:
        case MathKeyboardInput.F:
        case MathKeyboardInput.G:
        case MathKeyboardInput.H:
        case MathKeyboardInput.I:
        case MathKeyboardInput.J:
        case MathKeyboardInput.K:
        case MathKeyboardInput.L:
        case MathKeyboardInput.M:
        case MathKeyboardInput.N:
        case MathKeyboardInput.O:
        case MathKeyboardInput.P:
        case MathKeyboardInput.Q:
        case MathKeyboardInput.R:
        case MathKeyboardInput.S:
        case MathKeyboardInput.T:
        case MathKeyboardInput.U:
        case MathKeyboardInput.V:
        case MathKeyboardInput.W:
        case MathKeyboardInput.X:
        case MathKeyboardInput.Y:
        case MathKeyboardInput.Z:
        case MathKeyboardInput.SmallA:
        case MathKeyboardInput.SmallB:
        case MathKeyboardInput.SmallC:
        case MathKeyboardInput.SmallD:
        case MathKeyboardInput.SmallE:
        case MathKeyboardInput.SmallF:
        case MathKeyboardInput.SmallG:
        case MathKeyboardInput.SmallH:
        case MathKeyboardInput.SmallI:
        case MathKeyboardInput.SmallJ:
        case MathKeyboardInput.SmallK:
        case MathKeyboardInput.SmallL:
        case MathKeyboardInput.SmallM:
        case MathKeyboardInput.SmallN:
        case MathKeyboardInput.SmallO:
        case MathKeyboardInput.SmallP:
        case MathKeyboardInput.SmallQ:
        case MathKeyboardInput.SmallR:
        case MathKeyboardInput.SmallS:
        case MathKeyboardInput.SmallT:
        case MathKeyboardInput.SmallU:
        case MathKeyboardInput.SmallV:
        case MathKeyboardInput.SmallW:
        case MathKeyboardInput.SmallX:
        case MathKeyboardInput.SmallY:
        case MathKeyboardInput.SmallZ:
          InsertAtom(LaTeXDefaults.ForAscii(checked((sbyte)input))
            ?? throw new InvalidCodePathException
              ($"Invalid LaTeX character {input} was handled by ascii case"));
          break;
        case MathKeyboardInput.Alpha:
        case MathKeyboardInput.Beta:
        case MathKeyboardInput.Gamma:
        case MathKeyboardInput.Delta:
        case MathKeyboardInput.Epsilon:
        case MathKeyboardInput.Zeta:
        case MathKeyboardInput.Eta:
        case MathKeyboardInput.Theta:
        case MathKeyboardInput.Iota:
        case MathKeyboardInput.Kappa:
        case MathKeyboardInput.Lambda:
        case MathKeyboardInput.Mu:
        case MathKeyboardInput.Nu:
        case MathKeyboardInput.Xi:
        case MathKeyboardInput.Omicron:
        case MathKeyboardInput.Pi:
        case MathKeyboardInput.Rho:
        case MathKeyboardInput.Sigma:
        case MathKeyboardInput.Tau:
        case MathKeyboardInput.Upsilon:
        case MathKeyboardInput.Phi:
        case MathKeyboardInput.Chi:
        case MathKeyboardInput.Psi:
        case MathKeyboardInput.Omega:
        case MathKeyboardInput.SmallAlpha:
        case MathKeyboardInput.SmallBeta:
        case MathKeyboardInput.SmallGamma:
        case MathKeyboardInput.SmallDelta:
        case MathKeyboardInput.SmallEpsilon:
        case MathKeyboardInput.SmallZeta:
        case MathKeyboardInput.SmallEta:
        case MathKeyboardInput.SmallTheta:
        case MathKeyboardInput.SmallIota:
        case MathKeyboardInput.SmallKappa:
        case MathKeyboardInput.SmallLambda:
        case MathKeyboardInput.SmallMu:
        case MathKeyboardInput.SmallNu:
        case MathKeyboardInput.SmallXi:
        case MathKeyboardInput.SmallOmicron:
        case MathKeyboardInput.SmallPi:
        case MathKeyboardInput.SmallRho:
        case MathKeyboardInput.SmallSigma:
        case MathKeyboardInput.SmallSigma2:
        case MathKeyboardInput.SmallTau:
        case MathKeyboardInput.SmallUpsilon:
        case MathKeyboardInput.SmallPhi:
        case MathKeyboardInput.SmallChi:
        case MathKeyboardInput.SmallPsi:
        case MathKeyboardInput.SmallOmega:
          // All Greek letters are rendered as variables.
          InsertAtom(new Atoms.Variable(((char)input).ToStringInvariant()));
          break;
        default:
          break;
      }
      InsertionPointChanged();
    }

    public void MoveCaretToPoint(PointF point) {
      point.Y *= -1; //inverted canvas, blah blah
      InsertionIndex = ClosestIndexToPoint(point) ?? MathListIndex.Level0Index(MathList.Atoms.Count);
      ShowCaret = true;
    }

    /// <summary>Helper method to update caretView when insertion point/selection changes.</summary>
    private void InsertionPointChanged() {
      static void VisualizePlaceholders(MathList? mathList) {
        foreach (var mathAtom in mathList?.Atoms as IList<MathAtom> ?? Array.Empty<MathAtom>()) {
          if (mathAtom.Superscript is MathList super)
            VisualizePlaceholders(super);
          if (mathAtom.Subscript is MathList sub)
            VisualizePlaceholders(sub);
          switch (mathAtom) {
            case Atoms.Placeholder _:
              mathAtom.Nucleus = "\u25A1";
              break;
            case IMathListContainer container:
              foreach (var list in container.InnerLists)
                VisualizePlaceholders(list);
              break;
          }
        }
      }
      VisualizePlaceholders(MathList);
      if (MathList.AtomAt(_insertionIndex) is Atoms.Placeholder placeholder) {
        placeholder.Nucleus = "\u25A0";
        ShowCaret = false;
      } else {
        // Find the insert point rect and create a caretView to draw the caret at this position.
        ShowCaret = true;
      }
      // Check that we were returned a valid position before displaying a caret there.
      RecreateDisplayFromMathList();
      RedrawRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() {
      MathList.Clear();
      InsertionIndex = MathListIndex.Level0Index(0);
    }

    // Insert a list at a given point.
    public void InsertMathList(MathList list, PointF point) {
      var detailedIndex = ClosestIndexToPoint(point) ?? MathListIndex.Level0Index(0);
      // insert at the given index - but don't consider sublevels at this point
      var index = MathListIndex.Level0Index(detailedIndex.AtomIndex);
      foreach (var atom in list.Atoms) {
        MathList.InsertAndAdvance(ref index, atom, MathListSubIndexType.None);
      }
      InsertionIndex = index; // move the index to the end of the new list.
    }

    public void HighlightCharacterAt(MathListIndex index, Structures.Color color) {
      // setup highlights before drawing the MTLine
      Display?.HighlightCharacterAt(index, color);
      RedrawRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHighlights() {
      RecreateDisplayFromMathList();
      RedrawRequested?.Invoke(this, EventArgs.Empty);
    }
  }
}
