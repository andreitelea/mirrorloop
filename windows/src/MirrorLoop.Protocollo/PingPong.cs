namespace MirrorLoop.Protocollo;

/// <summary>
/// Il PC chiede all'iPhone "che ore sono per te?" (tipo 3). Serve a calcolare di quanto i due
/// orologi sono spostati, come fanno i server dell'ora di Internet (NTP).
/// </summary>
/// <param name="Numero">Numero progressivo, per riconoscere la risposta.</param>
/// <param name="T1">Ora del PC alla partenza del ping, in microsecondi dal 1/1/1970 UTC.</param>
public sealed record Ping(int Numero, long T1) : Messaggio;

/// <summary>
/// La risposta dell'iPhone a un <see cref="Ping"/> (tipo 4). Le ore T2 e T3 sono dell'orologio
/// dell'iPhone, lo stesso usato per gli istanti di cattura di video e audio.
/// </summary>
/// <param name="Numero">Lo stesso numero del ping.</param>
/// <param name="T1">L'ora del PC copiata dal ping.</param>
/// <param name="T2">Ora dell'iPhone all'arrivo del ping.</param>
/// <param name="T3">Ora dell'iPhone alla partenza della risposta.</param>
public sealed record Pong(int Numero, long T1, long T2, long T3) : Messaggio;
