# Boot screen nativa UWP

## Contrato de apresentacao

O `MainPage` mostra a sobreposicao XAML `BootOverlay` assim que o frontend
aceita a inicializacao de um titulo. Ela bloqueia a entrada da biblioteca,
mas nao bloqueia a entrada que sera entregue ao jogo depois que o CoreHost
estiver pronto.

Estados implementados:

```
Home -> Preparing -> LoadingAssets -> LoadingShaders -> StartingGuest
     -> WaitingFirstFrame -> FadeOut (500 ms) -> Hidden
```

`RyujinxUwpCoreHost_HasFirstVisiblePresent` e a unica condicao que inicia o
fade. Inicio da thread do guest, atividade do FIFO e timeout nao fecham a
tela. O frontend memoriza esse evento por sessao porque o CoreHost pode parar
antes de a interface consultar seu estado novamente.

## Falhas antes do primeiro quadro

Falhas de keys, firmware, arquivo brokered, start do CoreHost ou termino do
guest antes do primeiro present mudam o estado para `Error`. A tela mostra
`[A] Tentar novamente` e `[B] Voltar a biblioteca`; A exige soltura e novo
pressionamento para nao reutilizar o botao que iniciou o titulo. Nao ha
timeout para converter uma inicializacao lenta em erro.

## Assets

O fallback esta ativo: fundo preto, logo Ryubox e spinner. A imagem reservada
para o filme de startup fica oculta ate receber um asset validado.

A extracao de `NintendoLogo.png` e `StartupMovie.gif` da Logo Partition ainda
nao foi implementada. Ela deve ser feita no CoreHost, apos abrir o Program NCA
com as keys do usuario, e gravar somente os dois arquivos necessarios no cache
privado por TitleId. Nao copiar a NCA inteira ao cache, nao depender do
frontend desktop e nao assumir que todos os titulos contem GIF. A integracao
so podera trocar o fallback depois de validar: TitleId, formato da imagem,
limites de tamanho, limpeza na desinstalacao e delays reais de cada frame GIF.
