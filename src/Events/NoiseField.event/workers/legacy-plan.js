// The package chooses its own flipbook resolution under the host's validated output budget.
function updateController(input) {
  var f=Math.fround;
  function roundEven(x) {
    var lo=Math.floor(x), part=x-lo;
    return part<.5?lo:part>.5?lo+1:(lo%2===0?lo:lo+1);
  }
  function clamp(x,lo,hi) { return Math.max(lo,Math.min(hi,x)); }
  var frames=clamp(input.frameCount,2,128), sampleRate=clamp(input.sampleRate,.1,8);
  var block=clamp(roundEven(f(f(1.5)/f(sampleRate))),1,16);
  var margin=Math.max(0,input.margin);
  var totalW=Math.max(.001,f(f(input.widthWorld)+f(f(margin)*f(2))));
  var totalH=Math.max(.001,f(f(input.heightWorld)+f(f(margin)*f(2))));
  var pxW=clamp(roundEven(f(f(input.targetPixelWidth)/f(96))),1,12)*96;
  var aspect=f(totalH/totalW), pxH=Math.max(16,roundEven(f(f(pxW)*aspect)));
  var maxCells=Math.max(8,Math.floor(1024/block));
  function cells(size) { return clamp(roundEven(f(f(size)/f(block))),8,maxCells); }
  var cellsX=cells(pxW), cellsY=cells(pxH);
  while(cellsX*block*cellsY*block*frames>4200000 && pxW>192) {
    pxW=Math.max(192,roundEven(f(f(pxW)*f(.8))));
    pxH=Math.max(16,roundEven(f(f(pxW)*aspect)));
    cellsX=cells(pxW); cellsY=cells(pxH);
  }
  while(cellsX*block*cellsY*block*frames>4200000 && frames>2) frames--;
  return {width:cellsX*block,height:cellsY*block,frames:frames};
}
