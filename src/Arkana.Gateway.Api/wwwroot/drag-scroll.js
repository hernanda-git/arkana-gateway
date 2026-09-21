// Horizontal drag-to-scroll for any container with [data-drag-scroll]
// No dependencies, auto-initializes, handles dynamic content.
(function () {
  'use strict';

  function initContainer(el) {
    let isDown = false;
    let startX = 0;
    let scrollLeft = 0;

    function onMouseDown(e) {
      isDown = true;
      el.classList.add('dragging');
      startX = e.pageX - el.offsetLeft;
      scrollLeft = el.scrollLeft;
    }

    function onMouseLeave() {
      isDown = false;
      el.classList.remove('dragging');
    }

    function onMouseUp() {
      isDown = false;
      el.classList.remove('dragging');
    }

    function onMouseMove(e) {
      if (!isDown) return;
      e.preventDefault();
      const x = e.pageX - el.offsetLeft;
      const walk = (x - startX) * 1.5;
      el.scrollLeft = scrollLeft - walk;
    }

    el.addEventListener('mousedown', onMouseDown);
    el.addEventListener('mouseleave', onMouseLeave);
    el.addEventListener('mouseup', onMouseUp);
    el.addEventListener('mousemove', onMouseMove);

    el._dragCleanup = function () {
      el.removeEventListener('mousedown', onMouseDown);
      el.removeEventListener('mouseleave', onMouseLeave);
      el.removeEventListener('mouseup', onMouseUp);
      el.removeEventListener('mousemove', onMouseMove);
    };
  }

  // Init all existing
  document.querySelectorAll('[data-drag-scroll]').forEach(initContainer);

  // Observe new elements added dynamically (Blazor re-renders)
  const observer = new MutationObserver(function (mutations) {
    mutations.forEach(function (mutation) {
      mutation.addedNodes.forEach(function (node) {
        if (node.nodeType === 1) {
          if (node.hasAttribute && node.hasAttribute('data-drag-scroll')) {
            initContainer(node);
          }
          node.querySelectorAll && node.querySelectorAll('[data-drag-scroll]').forEach(initContainer);
        }
      });
    });
  });
  observer.observe(document.body, { childList: true, subtree: true });
})();
