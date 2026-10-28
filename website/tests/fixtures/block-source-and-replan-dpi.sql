  procedure block_source_and_replan_dpi (
      id_dpi type of column docplaceitem.id)
  returns (
      success smallint,
      message varchar(255))
  as
  declare variable idtaskitem type of column taskitem.id;
  declare variable iddoc type of column doc.id;
  declare variable itypedocplace type of column docplaceitem.itypedocplace;
  declare variable itypedoc type of column doc.itypedoc;
  declare variable idstage type of column typedoc_plane_stage.id;
  declare variable istage type of column typedoc_plane_stage.istage;
  declare variable saved_istage type of column typedoc_plane_stage.istage;
  declare variable stage_count integer;
  declare variable qty_task type of column taskitem.qty;
  declare variable dyte_task type of column taskitem.dyte;
  declare variable staged boolean;
  declare variable itypeplanning type of column typedoc.itypeplanning;
  begin
    if (not exists(select 1 from taskitem_docplaceitem l where l.iddocplaceitem = :id_dpi)) then
      exception e_error 'Assembly replan: DPI has no parent TASKITEM';

    select dpi.itypedocplace from docplaceitem dpi where dpi.id = :id_dpi
    into :itypedocplace;
    -- Lock parent rows before DPI, as the normal TASKITEM planner does.
    for select ti.id from taskitem ti
        where exists(select 1 from taskitem_docplaceitem l
                     where l.idtaskitem = ti.id and l.iddocplaceitem = :id_dpi)
        order by ti.id with lock into :idtaskitem
    do
    begin
      select d.itypedoc, td.use_staged_planning, td.itypeplanning, ti.qty, ti.dyte
      from taskitem ti
      join doc d on d.id = ti.iddoc
      join typedoc td on td.id = d.itypedoc
      where ti.id = :idtaskitem
      into :itypedoc, :staged, :itypeplanning, :qty_task, :dyte_task;
      if (staged and itypeplanning is distinct from 92) then
      begin
        select count(*), min(ps.id), min(ps.istage)
        from typedoc_plane_stage ps
        where ps.itypedoc = :itypedoc and ps.itypedocplace = :itypedocplace
        into :stage_count, :idstage, :istage;
        if (stage_count <> 1) then
          exception e_error 'Assembly replan: DPI must match exactly one planning stage';
        if (qty_task is null and dyte_task is null) then
          exception e_error 'Assembly replan: staged goods require a known TASKITEM quantity';
        saved_istage = null;
        select ps.istage from taskitem_plane_stage saved
        join typedoc_plane_stage ps on ps.id = saved.idtypedoc_plane_stage
        where saved.idtaskitem = :idtaskitem and ps.itypedoc = :itypedoc
        into :saved_istage;
        if (saved_istage is null or saved_istage > istage) then
          -- This is a seed, not an additional remainder. For quantitative goods,
          -- GET_STAGE_QTY_TARGET recalculates the target from corrected DPI.
          update or insert into taskitem_plane_stage (idtaskitem, idtypedoc_plane_stage, qty_left)
          values (:idtaskitem, :idstage, coalesce(:qty_task, 1))
          matching (idtaskitem);
      end
    end

    execute procedure pkdpusr.cancel_dpi_and_block_from(:id_dpi);

    for select l.idtaskitem from taskitem_docplaceitem l
        join taskitem ti on ti.id = l.idtaskitem
        join doc d on d.id = ti.iddoc
        join typedoc td on td.id = d.itypedoc
        where l.iddocplaceitem = :id_dpi and td.itypeplanning is distinct from 92
        order by l.idtaskitem
        into :idtaskitem
    do
    begin
      update taskitem set bplaneallow = 0 where id = :idtaskitem;
      update taskitem set bplaneallow = 1 where id = :idtaskitem;
    end
    -- Mode 92 plans stages across the whole document, not one TASKITEM.
    for select distinct d.id from taskitem_docplaceitem l
        join taskitem ti on ti.id = l.idtaskitem
        join doc d on d.id = ti.iddoc
        join typedoc td on td.id = d.itypedoc
        where l.iddocplaceitem = :id_dpi and td.itypeplanning = 92
        order by d.id
        into :iddoc
    do
    begin
      update doc set bplaneallow = 0 where id = :iddoc;
      update doc set bplaneallow = 1 where id = :iddoc;
    end
    success = 1;
    message = 'Товар заблокирован. Разница отправлена в планирование';
    suspend;
  end
