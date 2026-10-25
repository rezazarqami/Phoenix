window.signalDrawingTools = (() => {
  const ns = 'http://www.w3.org/2000/svg';
  const drawings = [];
  let chart, series, host, svg, tool = 'cursor', firstPoint = null, hoverPoint = null;

  const element = (name, attrs = {}) => {
    const node = document.createElementNS(ns, name);
    Object.entries(attrs).forEach(([key, value]) => node.setAttribute(key, value));
    return node;
  };

  function priceLabel(value) {
    return new Intl.NumberFormat('fa-IR', { maximumFractionDigits: 6 }).format(value);
  }

  function coordinates(point) {
    return { x: chart.timeScale().timeToCoordinate(point.time), y: series.priceToCoordinate(point.price) };
  }

  function logarithmicLevel(from, to, ratio) {
    return Math.exp(Math.log(from) + ratio * (Math.log(to) - Math.log(from)));
  }

  function drawTrend(group, drawing, preview = false) {
    const a = coordinates(drawing.a), b = coordinates(drawing.b);
    if (a.x == null || a.y == null || b.x == null || b.y == null) return;
    group.append(element('line', { x1:a.x, y1:a.y, x2:b.x, y2:b.y, class:`trend${preview?' preview':''}` }));
    group.append(element('circle', { cx:a.x, cy:a.y, r:3, class:'anchor' }));
    group.append(element('circle', { cx:b.x, cy:b.y, r:3, class:'anchor' }));
  }

  function drawFib(group, drawing, preview = false) {
    const a = coordinates(drawing.a), b = coordinates(drawing.b);
    if (a.x == null || b.x == null) return;
    const left = Math.min(a.x, b.x), right = Math.max(a.x, b.x);
    [0, .236, .382, .5, .618, .786, 1].forEach(ratio => {
      const price = logarithmicLevel(drawing.a.price, drawing.b.price, ratio);
      const y = series.priceToCoordinate(price);
      if (y == null) return;
      group.append(element('line', { x1:left, y1:y, x2:right, y2:y, class:`${ratio===0||ratio===1?'fib-main':'fib'}${preview?' preview':''}` }));
      const label = element('text', { x:right + 5, y:y - 4, class:preview?'preview':'' });
      label.textContent = `${ratio}  ${priceLabel(price)}`;
      group.append(label);
    });
    if (a.y != null) group.append(element('circle', { cx:a.x, cy:a.y, r:3, class:'anchor' }));
    if (b.y != null) group.append(element('circle', { cx:b.x, cy:b.y, r:3, class:'anchor' }));
  }

  function render() {
    if (!svg || !chart || !series) return;
    svg.setAttribute('viewBox', `0 0 ${host.clientWidth} ${host.clientHeight}`);
    svg.replaceChildren();
    drawings.forEach(drawing => drawing.type === 'trend' ? drawTrend(svg, drawing) : drawFib(svg, drawing));
    if (firstPoint && hoverPoint) {
      const preview = { type:tool, a:firstPoint, b:hoverPoint };
      tool === 'trend' ? drawTrend(svg, preview, true) : drawFib(svg, preview, true);
    }
  }

  function pointFromEvent(event) {
    const rect = svg.getBoundingClientRect();
    const x = event.clientX - rect.left, y = event.clientY - rect.top;
    const time = chart.timeScale().coordinateToTime(x), price = series.coordinateToPrice(y);
    return time == null || price == null || price <= 0 ? null : { time, price };
  }

  function setTool(next) {
    tool = next;
    firstPoint = hoverPoint = null;
    document.querySelectorAll('.chart-tools [data-tool]').forEach(button => button.classList.toggle('active', button.dataset.tool === tool));
    if (svg) svg.classList.toggle('drawing', tool !== 'cursor');
    render();
  }

  function attach(nextChart, nextSeries) {
    chart = nextChart; series = nextSeries; host = document.querySelector('#signalChart');
    drawings.length = 0; firstPoint = hoverPoint = null;
    svg = element('svg', { class:'signal-drawing-layer', 'aria-label':'لایه ابزارهای ترسیم نمودار' });
    host.append(svg);
    svg.addEventListener('click', event => {
      if (tool === 'cursor') return;
      const point = pointFromEvent(event); if (!point) return;
      if (!firstPoint) firstPoint = point;
      else { drawings.push({ type:tool, a:firstPoint, b:point }); firstPoint = hoverPoint = null; }
      render();
    });
    svg.addEventListener('mousemove', event => { if (firstPoint) { hoverPoint = pointFromEvent(event); render(); } });
    svg.addEventListener('mouseleave', () => { hoverPoint = null; render(); });
    chart.timeScale().subscribeVisibleTimeRangeChange(render);
    chart.timeScale().subscribeVisibleLogicalRangeChange(render);
    setTool(tool);
  }

  document.addEventListener('click', event => {
    const button = event.target.closest('.chart-tools button'); if (!button) return;
    if (button.dataset.tool) setTool(button.dataset.tool);
    if (button.id === 'undoDrawing') { drawings.pop(); firstPoint = hoverPoint = null; render(); }
    if (button.id === 'clearDrawings') { drawings.length = 0; firstPoint = hoverPoint = null; render(); }
  });
  document.addEventListener('keydown', event => { if (event.key === 'Escape') setTool('cursor'); });
  window.addEventListener('resize', render);
  return { attach };
})();
