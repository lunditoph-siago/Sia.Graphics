namespace Sia.Graphics.Reactive;

public delegate void RenderGraphPassDeclaration(
    RenderGraphPassDeclarationBuilder pass);

public delegate void RenderGraphPassDeclaration<TDependencies>(
    in TDependencies dependencies,
    RenderGraphPassDeclarationBuilder pass);
