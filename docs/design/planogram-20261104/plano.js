window.PLANO=(function(){
const PITCH=25, HOLE0=25, BAY_W=900, INNER_W=870, BOARD_H=1050, BASE_H=150, TOTAL_H=1200, UP_W=30;
const HOOK={len:200,drop:50,span:2,label:[52,26],tagAboveArm:10}; const HANG=10;
const P={
 p1:{code:'XD0001201',name:'胶棒 2 支装',en:'Glue Stick 2pk',w:70,h:150,t:25,q:7,price:2.5,c:'#ffd8bf'},
 p2:{code:'XD0001202',name:'圆珠笔 10 支',en:'Ballpoint Pen 10pk',w:90,h:185,t:20,q:9,price:3.5,c:'#bae0ff'},
 p3:{code:'XD0001203',name:'剪刀 170mm',en:'Scissors 170mm',w:95,h:215,t:15,q:12,price:4,c:'#d9f7be'},
 p4:{code:'XD0001204',name:'荧光笔 6 色',en:'Highlighter 6pk',w:130,h:165,t:18,q:10,price:5,c:'#fff1b8'},
 p5:{code:'XD0001205',name:'便利贴 3 本',en:'Sticky Notes 3pk',w:80,h:105,t:30,q:6,price:2,c:'#ffccc7'},
 p6:{code:'XD0001206',name:'修正带 2 个',en:'Correction Tape 2pk',w:75,h:150,t:25,q:7,price:3,c:'#d3adf7'},
 p7:{code:'XD0001207',name:'橡皮 3 个装',en:'Eraser 3pk',w:65,h:120,t:22,q:8,price:1.5,c:'#b5f5ec'},
 p8:{code:'XD0001208',name:'马克笔 4 色',en:'Marker 4pk',w:115,h:190,t:20,q:9,price:4.5,c:'#ffd6e7'},
 p9:{code:'XD0001209',name:'尺子 30cm',en:'Ruler 30cm',w:45,h:330,t:5,q:20,price:1.5,c:'#f4ffb8'},
 p10:{code:'XD0001210',name:'胶带 2 卷',en:'Tape 2pk',w:100,h:150,t:55,q:3,price:3.5,c:'#ffe7ba'},
 p11:{code:'XD0001211',name:'笔记本 A6',en:'Notebook A6',w:110,h:155,t:12,q:15,price:2.5,c:'#fff1b8'},
 p12:{code:'XD0001212',name:'订书机',en:'Stapler',w:90,h:200,t:40,q:4,price:6,c:'#d6e4ff'},
};
const items=[
 [0,'p3',1,2],[0,'p3',6,2],[0,'p2',11,2],[0,'p2',15,2],[0,'p8',20,2],[0,'p4',26,2],[0,'p12',31,2],
 [0,'p1',1,12],[0,'p1',5,12],[0,'p1',9,12],[0,'p6',13,12],[0,'p6',17,12],[0,'p11',22,12],[0,'p11',27,12],[0,'p7',31,12],
 [0,'p5',1,22],[0,'p5',5,22],[0,'p5',9,22],[0,'p5',13,22],[0,'p10',17,22],[0,'p10',22,22],[0,'p10',27,22],[0,'p7',31,22],
 [0,'p11',1,31],[0,'p11',6,31],[0,'p1',10,31],[0,'p1',14,31],[0,'p7',18,31],[0,'p7',21,31],[0,'p7',24,31],[0,'p2',28,31],
 [1,'p8',1,2],[1,'p8',6,2],[1,'p8',11,2],[1,'p4',17,2],[1,'p4',23,2],[1,'p9',30,2],
 [1,'p2',1,12],[1,'p2',5,12],[1,'p12',9,12],[1,'p3',14,12],[1,'p3',19,12],[1,'p6',23,12],
 [1,'p5',1,22],[1,'p5',5,22],[1,'p5',9,22],[1,'p10',13,22],[1,'p1',17,22],[1,'p1',21,22],[1,'p11',25,22],
 [1,'p7',1,31],[1,'p7',4,31],
];
function render(o){
  const S=o.scale, OX=o.ox??34, OY=o.oy??40, print=!!o.print, bays=2, en=o.lang==='en';
  const W=BAY_W*bays*S+OX+10, H=TOTAL_H*S+OY+10;
  let svg=`<svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" xmlns="http://www.w3.org/2000/svg" font-family="-apple-system,BlinkMacSystemFont,PingFang SC,Segoe UI,Roboto,Helvetica Neue,Arial,Microsoft YaHei,sans-serif">`;
  svg+=`<defs><pattern id="holes${S}" width="${PITCH*S}" height="${PITCH*S}" patternUnits="userSpaceOnUse"><circle cx="${PITCH*S/2}" cy="${PITCH*S/2}" r="${print?1.1:1.5}" fill="${print?'#d0d0d0':'#cfcfcf'}"/></pattern></defs>`;
  const bx=OX, by=OY;
  if(o.rulers!==false){
    svg+=`<rect x="0" y="0" width="${W}" height="${OY-14}" fill="${print?'#fff':'#fafafa'}"/><rect x="0" y="0" width="${OX-8}" height="${H}" fill="${print?'#fff':'#fafafa'}"/>`;
    for(let mm=0;mm<=BAY_W*bays;mm+=100){const x=OX+mm*S;svg+=`<line x1="${x}" y1="${OY-22}" x2="${x}" y2="${OY-14}" stroke="#bfbfbf"/>`;if(mm%300==0)svg+=`<text x="${x+2}" y="${OY-24}" font-size="9" fill="rgba(0,0,0,.45)">${mm}</text>`;}
    for(let mm=0;mm<=TOTAL_H;mm+=100){const y=OY+mm*S;svg+=`<line x1="${OX-16}" y1="${y}" x2="${OX-8}" y2="${y}" stroke="#bfbfbf"/>`;if(mm%300==0)svg+=`<text x="2" y="${y+3}" font-size="9" fill="rgba(0,0,0,.45)">${mm}</text>`;}
  }
  for(let b=0;b<bays;b++){const x=OX+b*BAY_W*S;svg+=`<text x="${x+BAY_W*S/2}" y="${OY-4}" font-size="${print?12:11}" text-anchor="middle" fill="rgba(0,0,0,.75)" font-weight="600">${en?'Bay':'节'} ${b+1} · 900 mm</text>`;}
  svg+=`<rect x="${bx}" y="${by}" width="${BAY_W*bays*S}" height="${TOTAL_H*S}" fill="#f7f7f7" stroke="#bfbfbf"/>`;
  for(let b=0;b<bays;b++){const px=bx+b*BAY_W*S+(BAY_W-INNER_W)/2*S;
    svg+=`<rect x="${px}" y="${by}" width="${INNER_W*S}" height="${BOARD_H*S}" fill="#fff" stroke="#d9d9d9"/>`;
    svg+=`<rect x="${px+(HOLE0-PITCH/2)*S}" y="${by+(HOLE0-PITCH/2)*S}" width="${34*PITCH*S}" height="${41*PITCH*S}" fill="url(#holes${S})"/>`;
    if(o.coords){ // 打印版：孔位列号（每 5 列）与行号（每 5 行）
      for(let c=0;c<34;c+=5){const x=px+(HOLE0+c*PITCH)*S;svg+=`<text x="${x}" y="${by+BOARD_H*S+10}" font-size="7" text-anchor="middle" fill="#1677ff">${c+1}</text>`;}
      if(b===0)for(let r=0;r<41;r+=5){const y=by+(HOLE0+r*PITCH)*S;svg+=`<text x="${bx+UP_W*S/2}" y="${y+2.5}" font-size="7" text-anchor="middle" fill="#1677ff" font-weight="600">${r+1}</text>`;}
    }
  }
  for(let i=0;i<=bays;i++){const x=bx+i*BAY_W*S-(i==bays?UP_W*S:0)-(i>0&&i<bays?UP_W*S/2:0);svg+=`<rect x="${x}" y="${by-2}" width="${UP_W*S}" height="${TOTAL_H*S+2}" fill="#e8e8e8" stroke="#bfbfbf"/>`;}
  svg+=`<rect x="${bx}" y="${by+BOARD_H*S}" width="${BAY_W*bays*S}" height="${BASE_H*S}" fill="#ededed" stroke="#bfbfbf"/>`;
  if(o.coords){for(let r=0;r<41;r+=5){const y=by+(HOLE0+r*PITCH)*S;svg+=`<text x="${bx+UP_W*S/2}" y="${y+2.5}" font-size="7" text-anchor="middle" fill="#1677ff" font-weight="600">${r+1}</text>`;}}
  svg+=`<text x="${bx+BAY_W*bays*S/2}" y="${by+(BOARD_H+BASE_H/2)*S+4}" font-size="10" text-anchor="middle" fill="rgba(0,0,0,.45)">${en?'Base shelf · 300 mm deep':'底板 · 深 300 mm'}</text>`;
  function holeXY(bay,col,row){const px=bx+bay*BAY_W*S+(BAY_W-INNER_W)/2*S;return [px+(HOLE0+col*PITCH)*S, by+(HOLE0+row*PITCH)*S];}
  let g='', n=0;
  for(const it of items){ n++;
    const [bay,pid,col,row]=it, p=P[pid]; const [hx,hy]=holeXY(bay,col,row);
    const cx=hx+(HOOK.span-1)*PITCH*S/2, w=p.w*S,h=p.h*S, x=cx-w/2, y=hy+(HOOK.drop-HANG)*S;
    g+=`<circle cx="${hx}" cy="${hy}" r="${1.6*S/0.4}" fill="#595959"/><circle cx="${hx+PITCH*S}" cy="${hy}" r="${1.6*S/0.4}" fill="#595959"/>`;
    g+=`<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="1.5" fill="${print?'#fff':p.c}" stroke="#595959" stroke-width="${print?.9:1}"/>`;
    g+=`<rect x="${cx-3}" y="${y+1.5}" width="6" height="2.2" rx="1" fill="#fff" stroke="#999" stroke-width=".6"/>`;
    const fs=print?(en?7.5:8.5):7.5; const src=en?p.en:p.name; const cw=en?fs*0.56:fs; const maxCh=Math.max(2,Math.floor((w-4)/cw));
    let lines=[];
    if(en){ // 英文按单词折行，最多 3 行，超出加省略号
      let cur='';for(const wd of src.split(' ')){const t=cur?cur+' '+wd:wd;if(t.length<=maxCh)cur=t;else{if(cur)lines.push(cur);cur=wd.length>maxCh?wd.slice(0,Math.max(1,maxCh-1))+'…':wd;}}
      if(cur)lines.push(cur); const maxLines=Math.max(1,Math.min(3,Math.floor((h-16)/(fs+2)))); if(lines.length>maxLines){lines=lines.slice(0,maxLines);lines[maxLines-1]=lines[maxLines-1].replace(/…?$/,'…');}
    } else { const raw=src.replace(/\s/g,''); lines=[raw.length>maxCh?raw.slice(0,maxCh):src]; }
    const y0=y+h/2+1-(lines.length-1)*(fs+2)/2;
    g+=`<text x="${cx}" font-size="${fs}" text-anchor="middle" fill="rgba(0,0,0,.8)">${lines.map((l,i)=>`<tspan x="${cx}" y="${y0+i*(fs+2)}">${l}</tspan>`).join('')}</text>`;
    g+=`<text x="${cx}" y="${y+h-3}" font-size="${print?7:6}" text-anchor="middle" fill="rgba(0,0,0,.55)" font-family="Menlo,Consolas,monospace">${print?p.code.slice(-4)+' ×'+p.q:p.code.slice(-4)+'·×'+p.q}</text>`;
    const [lw,lh]=HOOK.label, lx=cx-lw*S/2, ly=hy+(HOOK.drop-HOOK.tagAboveArm-lh)*S; // 价签顶边 = 挂孔 + (H − 底边高于挂货臂 − 价签高)
    g+=`<rect x="${lx}" y="${ly}" width="${lw*S}" height="${lh*S}" rx="1" fill="#fff" stroke="#8c8c8c" stroke-width=".7"/>`;
    g+=`<text x="${cx}" y="${ly+lh*S-2.6*S/0.4}" font-size="${6.5*S/0.4+1}" text-anchor="middle" font-weight="700" fill="#1f1f1f">$${p.price.toFixed(2)}</text>`;
    if(o.numbered){g+=`<circle cx="${x+1}" cy="${y+h-1}" r="6.5" fill="#1f1f1f"/><text x="${x+1}" y="${y+h+1.6}" font-size="7" text-anchor="middle" fill="#fff" font-weight="600">${n}</text>`;}
  }
  svg+=g+'</svg>';
  return {svg,width:W,height:H};
}
const stats=()=>{let units=0,area=0,val=0;const skus=new Set();for(const it of items){const p=P[it[1]];units+=p.q;area+=p.w*p.h;val+=p.q*p.price;skus.add(it[1]);}return {skus:skus.size,hooks:items.length,units,fill:Math.round(area/(INNER_W*BOARD_H*2)*100),val};};
return {P,items,render,stats,HOOK};
})();
