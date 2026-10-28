  procedure set_item_good (
      --отбор товара
      id_dp type of column docplace.id,
      id_dpi type of column docplaceitem.id,
      dyte type of column stockgrp.id,
      id_sg_from type of column stockgrp.id,
      id_sg_to type of column stockgrp.id,
      idgood type of column good.id,
      qty integer,
      datamatrix type of column taskitem_datamatrix.datamatrix,
      itype_datamatrix type of column taskitem_datamatrix.itype_datamatrix)
  returns (
      error boolean,
      error_msg varchar(200),
      qty_left integer)
  as
  declare variable id_dpei type of column docplaceexeitem.id;
  declare variable state_executed integer;
  declare variable id_docgrp_order type of column docgrp.id;
  declare variable iddimcomb type of column dimcomb.id;
  declare variable idocplaceaction type of column typedocplace.idocplaceaction;
  declare variable idtaskitem type of column taskitem.id;
  declare variable iddocgrp type of column docgrp.id;
  declare variable iddpig type of column docplaceitem.iddocplaceitemgroup;
  begin
    :error = false;
    :error_msg = '';
    :state_executed = pkdpstate.get_executed();

    if (char_length(:datamatrix) > 0) then
    begin
      if (exists(select td.idtaskitem from taskitem_datamatrix td where td.datamatrix = :datamatrix
                 and td.itype_datamatrix = :itype_datamatrix)) then
      begin
        :error = true;
        :error_msg = 'Данный КМ уже существует';
        suspend;
        exit;
      end
    end

    if ((:id_dpi is null) or (:id_dpi = 0)) then
      exit;

    if (:id_sg_to = 0) then
      :id_sg_to = null;

    if ((:idgood is null) or (:idgood = 0)) then
      exit;

    --отбор товаров(короба)
    select dpi.iddimcomb
         , tdp.idocplaceaction
         , ti.id
         , ti.iddoc
         , dpi.iddocplaceitemgroup
    from docplaceitem dpi
    join typedocplace tdp on tdp.id = dpi.itypedocplace
    left join taskitem_docplaceitem td on td.iddocplaceitem = dpi.id
    left join taskitem ti on ti.id = td.idtaskitem
    where dpi.id = :id_dpi
    into :iddimcomb, :idocplaceaction, :idtaskitem, :iddocgrp, :iddpig;

    select iddocplaceexeitem
    from pkdpusr.crt_itemexe_good(:id_dp, :id_dpi, :id_sg_from, iif(:idocplaceaction <> 4, iif(:id_sg_to is null, :dyte, :id_sg_to), null), :idgood, :iddimcomb, :qty, null, null)
    into :id_dpei;

    if (:id_dpei is null) then
    begin
      :error = true;
      :error_msg = 'Ошибка: Не удалось создать строчку факта';
    end
    else
    begin
      execute procedure pkdpusr.setstate_docplaceexeitem(:id_dpei, :state_executed, 0);

      select dpi.qtyplan - sum(coalesce(dpei.qtyfact, 0))
      from docplaceitem dpi
      join docplaceexeitem dpei on dpei.iddocplaceitem = dpi.id and
            dpei.state_docplaceexeitem <= :state_executed
      where dpi.id = :id_dpi
      group by dpi.qtyplan
      into :qty_left;
    end

    if ((:error = false) and (char_length(:datamatrix) > 0) and (:idtaskitem is not null)) then
    begin
      insert into taskitem_datamatrix (
          idtaskitem, datamatrix, iddoc, iddocplace, completed, iddocplaceitemgroup, iddocplaceexeitem, itype_datamatrix)
      values (:idtaskitem, :datamatrix, :iddocgrp, :id_dp, 0, :iddpig, :id_dpei, :itype_datamatrix);
    end

    suspend;
  end
