// 设计稿共用：SVG 货架片段绘制（单位 mm，比例 S = px / mm）
window.PH = ['#ffd8bf','#bae0ff','#d9f7be','#fff1b8','#ffccc7','#d3adf7','#b5f5ec','#ffd6e7','#ffe7ba','#d6e4ff'];
let uid = 0;
// 商品「照片」占位：渐变底 + 标签条 + 条纹，用来示意实拍图（设计稿里没有真实图片）
window.photo = (x, y, w, h, color, seed) => {
  const id = 'g' + (++uid);
  const label = ['#222','#1f4b99','#8c1d18','#0b6e4f','#5b2a86'][seed % 5];
  return `<defs><linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="${color}"/></linearGradient></defs>
  <rect x="${x}" y="${y}" width="${w}" height="${h}" fill="url(#${id})"/>
  <rect x="${x + w * .12}" y="${y + h * .18}" width="${w * .76}" height="${h * .34}" rx="2" fill="${label}" opacity=".88"/>
  <rect x="${x + w * .2}" y="${y + h * .26}" width="${w * .6}" height="${h * .05}" fill="#fff" opacity=".85"/>
  <rect x="${x + w * .2}" y="${y + h * .35}" width="${w * .4}" height="${h * .04}" fill="#fff" opacity=".6"/>
  <rect x="${x + w * .1}" y="${y + h * .62}" width="${w * .8}" height="${h * .05}" fill="${label}" opacity=".35"/>
  <rect x="${x + w * .1}" y="${y + h * .72}" width="${w * .55}" height="${h * .05}" fill="${label}" opacity=".25"/>`;
};
// items: {x,y,w,h,c(color idx),code,qty,name, st: 'n'|'swap'|'ghost'|'shift'|'bad'|'wrap', img(bool)}
window.board = (o) => {
  const S = o.S || 0.54, W = o.W || 1200, H = o.H || 620, pad = 8;
  const px = v => v * S;
  let s = `<svg width="${px(W) + pad * 2}" height="${px(H) + pad * 2}" xmlns="http://www.w3.org/2000/svg" font-family="-apple-system,PingFang SC,Segoe UI,Roboto,Arial,sans-serif">
  <defs><pattern id="hp${o.id || 0}" width="${px(25)}" height="${px(25)}" patternUnits="userSpaceOnUse"><circle cx="${px(12.5)}" cy="${px(12.5)}" r="1" fill="#cfcfcf"/></pattern></defs>
  <g transform="translate(${pad},${pad})"><rect width="${px(W)}" height="${px(H)}" fill="#fff" stroke="#d9d9d9"/><rect width="${px(W)}" height="${px(H)}" fill="url(#hp${o.id || 0})"/>
  <rect x="${-3}" y="0" width="6" height="${px(H)}" fill="#e8e8e8" stroke="#bfbfbf"/><rect x="${px(W) - 3}" y="0" width="6" height="${px(W) * 0 + px(H)}" fill="#e8e8e8" stroke="#bfbfbf"/>`;
  for (const it of o.items) {
    const x = px(it.x), y = px(it.y), w = px(it.w), h = px(it.h), c = PH[it.c % PH.length];
    const st = it.st || 'n';
    const stroke = { n: '#8c8c8c', swap: '#1677ff', ghost: '#52c41a', shift: '#1677ff', bad: '#ff4d4f', wrap: '#fa8c16', old: '#bfbfbf' }[st];
    const dash = (st === 'shift' || st === 'wrap' || st === 'ghost' || st === 'old') ? 'stroke-dasharray="5 3"' : '';
    const sw = st === 'n' ? 1 : 2;
    const op = (st === 'shift' || st === 'wrap') ? .85 : st === 'ghost' ? .78 : st === 'old' ? .3 : 1;
    s += `<g opacity="${op}">`;
    if (it.tag !== false) s += `<rect x="${x + w / 2 - px(26)}" y="${y - px(32)}" width="${px(52)}" height="${px(26)}" rx="1" fill="#fff" stroke="#8c8c8c" stroke-width=".7"/><text x="${x + w / 2}" y="${y - px(32) + px(21)}" text-anchor="middle" font-size="${Math.max(7, px(22))}" font-weight="700" fill="#1f1f1f">$${(it.price || 1.5).toFixed(2)}</text>`;
    s += `<circle cx="${x + w / 2 - px(12.5)}" cy="${y - px(44)}" r="1.8" fill="#595959"/><circle cx="${x + w / 2 + px(12.5)}" cy="${y - px(44)}" r="1.8" fill="#595959"/>`;
    s += `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="2" fill="${st === 'bad' ? '#fff2f0' : c}" />`;
    if (o.img !== false) s += photo(x, y, w, h, c, it.c);
    s += `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="2" fill="none" stroke="${stroke}" stroke-width="${sw}" ${dash}/>`;
    s += `<text x="${x + w / 2}" y="${y + h - 5}" text-anchor="middle" font-size="8.5" fill="rgba(0,0,0,.6)" font-family="Menlo,Consolas,monospace" style="paint-order:stroke;stroke:#fff;stroke-width:2.5px">${it.code || ''}${it.qty ? ' ×' + it.qty : ''}</text>`;
    if (o.img === false && it.name) s += `<text x="${x + w / 2}" y="${y + h / 2}" text-anchor="middle" font-size="9" fill="rgba(0,0,0,.75)">${it.name}</text>`;
    s += `</g>`;
  }
  s += (o.extra ? o.extra(px) : '') ;
  s += `</g></svg>`;
  return s;
};
